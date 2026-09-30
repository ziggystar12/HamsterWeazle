using System.Security.Cryptography;

namespace HamsterWeazle.Services;

public sealed record RescueResult(string ImagePath, string PassPath, int Recovered, int Remaining, int Conflicts);

public static class RescueReader
{
    public static async Task<RescueResult> ReadAsync(GwRunner runner, string sourceImage, GwOptions options)
    {
        var previous = CaptureStore.FindForImage(sourceImage)
            ?? throw new InvalidOperationException("This image needs a matching HamsterWeazle read report. Read the disk once in HamsterWeazle before using Rescue Read.");
        var layout = DiskLayout.ForFormat(previous.Format);
        if (layout == null || !layout.AcceptsContainer(sourceImage) || previous.ImageLength != layout.ImageSize)
            throw new InvalidOperationException("Rescue Read supports complete-sized, flat IBM PC, Amiga, Atari ST and MSX sector images. Flux and DMK captures remain separate originals.");
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourceImage))!;
        string name = Path.GetFileNameWithoutExtension(sourceImage), ext = Path.GetExtension(sourceImage);
        string pass = Path.Combine(directory, name + "-pass-" + id + ext);
        string combined = Path.Combine(directory, name + "-rescued-" + id + ext);
        // Always read a complete pass so sector positions retain their original meaning.
        var full = options with { StartCyl = 0, EndCyl = layout.Cylinders - 1, Raw = false };
        string command = runner.BuildArguments(GwOperation.Read, previous.Format, pass, full);
        int code = await runner.RunAsync(command);
        var current = runner.LastReport;
        if (code != 0 || current == null || current.Fatal || !File.Exists(pass) || new FileInfo(pass).Length != layout.ImageSize)
            throw new IOException("The new pass could not be combined. The original image and any partial pass have been retained.");
        return Merge(sourceImage, previous, pass, current, combined);
    }

    public static RescueResult Merge(string source, CaptureReport oldReport, string pass, CaptureReport newReport, string destination)
    {
        var layout = DiskLayout.ForFormat(oldReport.Format);
        if (layout == null || !layout.CanMerge || oldReport.Format != newReport.Format)
            throw new InvalidOperationException("The captures do not have the same supported sector layout.");
        foreach (var report in new[] { oldReport, newReport })
            if (report.SchemaVersion != 1 || report.Layout == null || report.Layout.Cylinders != layout.Cylinders
                || report.Layout.Heads != layout.Heads || report.Layout.BytesPerSector != layout.BytesPerSector
                || !report.Layout.Sectors.SequenceEqual(layout.Sectors)) throw new IOException("The saved report geometry does not match this disk format.");
        byte[] oldData = File.ReadAllBytes(source), newData = File.ReadAllBytes(pass);
        if (oldData.LongLength != layout.ImageSize || newData.LongLength != layout.ImageSize)
            throw new IOException("Both captures must match the full disk geometry.");
        string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
        if (Hash(oldData) != oldReport.ImageSha256 || Hash(newData) != newReport.ImageSha256)
            throw new IOException("A capture has changed since its report was saved; it cannot be safely combined.");
        bool[] good = oldReport.ValidSectors(), incoming = newReport.ValidSectors();
        if (good.Length != layout.SectorCount || incoming.Length != good.Length) throw new IOException("Sector validity information does not match the disk.");
        int recovered = 0;
        var disputed = oldReport.ConflictingSectorIndices.ToHashSet();
        for (int sector = 0; sector < good.Length; sector++)
        {
            int start = sector * layout.BytesPerSector;
            if (good[sector] && incoming[sector] && !oldData.AsSpan(start, layout.BytesPerSector).SequenceEqual(newData.AsSpan(start, layout.BytesPerSector))) disputed.Add(sector);
            if (good[sector] || !incoming[sector]) continue;
            newData.AsSpan(start, layout.BytesPerSector).CopyTo(oldData.AsSpan(start, layout.BytesPerSector));
            good[sector] = true; recovered++;
        }
        // CreateNew protects every prior capture, including an existing rescue output.
        string temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, oldData); File.Move(temp, destination, overwrite: false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        int conflicts = Math.Max(disputed.Count, oldReport.ConflictingSectors);
        var combined = new CaptureReport
        {
            Operation = "read", Format = oldReport.Format, ImagePath = destination, Layout = layout,
            Sources = [Path.GetFullPath(source), Path.GetFullPath(pass)], CollectedSectors = good, ConflictingSectors = conflicts,
            ConflictingSectorIndices = disputed.Order().ToList(),
            PreviousReports = new[] { oldReport.SavedReportPath, newReport.SavedReportPath }.OfType<string>().ToList(),
            Log = [$"Recovered {recovered} sectors; {good.Count(v => !v)} remain unknown or missing.",
                $"{conflicts} sectors differed between valid reads; earlier valid data was retained."]
        };
        for (int c = 0; c < layout.Cylinders; c++) for (int h = 0; h < layout.Heads; h++)
        {
            int count = layout.Sectors[layout.TrackIndex(c, h)], offset = layout.SectorOffset(c, h);
            int found = good.Skip(offset).Take(count).Count(v => v);
            int attempts = (oldReport.Tracks.GetValueOrDefault($"{c}.{h}")?.Attempts ?? 1)
                + (newReport.Tracks.GetValueOrDefault($"{c}.{h}")?.Attempts ?? 1);
            combined.Tracks[$"{c}.{h}"] = new TrackCapture { Cylinder = c, Head = h, Expected = count, Found = found, Attempts = attempts, GaveUp = found != count };
        }
        combined.Finish(0, false);
        CaptureStore.Save(combined);
        return new(destination, pass, recovered, good.Count(v => !v), conflicts);
    }
}
