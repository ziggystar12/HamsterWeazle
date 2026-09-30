using System.Buffers.Binary;
using System.Security.Cryptography;
using HamsterWeazle.Services;

internal static class Program
{
    private static int _checks;
    private static readonly string Work = Path.Combine(AppContext.BaseDirectory, "evidence-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { _checks++; return; }
        throw new Exception("Expected rejection: " + name);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Track(int c, int h, int found = 18) => $"T{c}.{h}: IBM MFM ({found}/18 sectors) from Raw Flux (135692 flux in 400.30ms)";
    private static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() is "read" or "write")
        {
            string path = args[^1];
            if (args[0] == "read")
            {
                File.WriteAllBytes(path, new byte[1474560]);
                Console.WriteLine(Track(0, 0));
                if (args.Contains("--slow")) { Console.Out.Flush(); await Task.Delay(30000); }
                Console.WriteLine(Track(0, 1));
            }
            else Console.WriteLine("All tracks verified");
            return 0;
        }
        try
        {
            Directory.CreateDirectory(Work);
            TestReports(); TestDetection(); TestFat12(); TestRescue(); await TestRunner();
            Console.WriteLine($"PASS: {_checks} checks — parser, confidence, reports, rescue, FAT12 and tool execution.");
            Console.WriteLine("Evidence: " + Work);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TestReports()
    {
        Check(DiskLayout.ForFormat("ibm.1440") is { Cylinders: 80, Heads: 2, SectorCount: 2880, ImageSize: 1474560, CanMerge: true }, "IBM geometry");
        var mac = DiskLayout.ForFormat("mac.800")!;
        Check(mac.Sectors[0] == 12 && mac.Sectors[48 * 2] == 9 && mac.SectorCount == 1600 && !mac.CanMerge, "variable Mac zones");
        Check(DiskLayout.ForFormat("amiga.amigados")?.ImageSize == 901120, "Amiga geometry");
        Check(DiskLayout.ForFormat("commodore.1541") is { Cylinders: 40, CanMerge: false }, "Commodore geometry");
        var report = CaptureReport.Replay(Track(0, 0, 9) + "\n" + Track(0, 0) + " (Retry #1.1)\n" + Track(0, 1, 0) + "\nT0.1: Giving up: 18 sectors missing", "ibm.1440");
        Check(report.SnapshotTracks()[0].State == TrackState.Retried && report.SnapshotTracks()[1].State == TrackState.Missing, "recovered and missing tracks retained");
        Check(report.ValidSectors().Take(18).All(v => v) && report.ValidSectors().Skip(18).All(v => !v), "cancelled/partial validity is conservative");
        var write = CaptureReport.Replay("T0.0: Writing Track (x)\n", "ibm.1440", "write");
        Check(write.SnapshotTracks()[0].State == TrackState.Written && write.Summary().Contains("not verified"), "written is distinct from verified");
        write.Feed("All tracks verified"); Check(write.SnapshotTracks()[0].State == TrackState.Verified, "positive verification evidence");
        var firstWrite = CaptureReport.Replay("T0.0: Writing Track\nT0.1: Writing Track\nFailed to verify Track 0.1", "ibm.1440", "write");
        firstWrite.ImagePath = "same.img";
        var resumed = new CaptureReport { Operation = "write", Format = "ibm.1440", ImagePath = "same.img" };
        resumed.ContinueFrom(firstWrite); resumed.Feed("T0.1: Writing Track"); resumed.Feed("All tracks verified");
        Check(resumed.SnapshotTracks().Count == 2 && resumed.SnapshotTracks().All(t => t.State == TrackState.Verified), "adaptive retry retains prior track results");
        Check(firstWrite.SnapshotTracks()[1].State == TrackState.Failed, "retry does not mutate the earlier report");
        var misplaced = CaptureReport.Replay(Track(1, 0) + "\nT1.0: Ignoring unexpected sector C:0 H:0 R:1 N:2", "ibm.1440");
        Check(misplaced.SnapshotTracks()[0].State == TrackState.HeaderMismatch && misplaced.ValidSectors().All(v => !v), "unexpected headers are not accepted data");
        Check(misplaced.ToText().Contains("possible format") && !misplaced.ToText().Contains("dead head"), "diagnosis remains a hypothesis");
        Check(FormatProbeResult.Evaluate("", "ibm.1440", 0).Score < 0, "empty probe cannot select first format");
        Check(!FormatProbeResult.Evaluate(Track(0, 0, 9), "ibm.1440", 0).Strong, "half a track is not strong");
        Check(!FormatProbeResult.Evaluate(Track(0, 0), "ibm.1440", 0).Strong, "one track is not sufficient confidence");
        string complete = string.Join('\n', from c in Enumerable.Range(0, 2) from h in Enumerable.Range(0, 2) select Track(c, h));
        Check(FormatProbeResult.Evaluate(complete, "ibm.1440", 0).Strong, "complete two-cylinder probe");
        Check(FormatProbeResult.Evaluate(complete, "ibm.1440", 1).Score < 0, "failed process cannot win");
        Check(FormatProbeResult.Evaluate(complete + "\n** FATAL ERROR:\nDisconnected", "ibm.1440", 0).Score < 0, "fatal zero-exit probe cannot win");
        Check(!FormatProbeResult.Evaluate(string.Join('\n', Enumerable.Range(0, 4).Select(c => Track(c, 0))), "ibm.1440", 0).Strong,
            "strong confidence requires both probe sides");
        var partial = CaptureReport.FromCommand("read --format ibm.1440 --tracks c=20-21 partial.img")!;
        partial.Feed("0. 0: .X");
        Check(partial.ValidSectors()[20 * 36] && !partial.ValidSectors()[0] && !partial.ValidSectors()[21 * 36],
            "partial range sector rows retain their cylinder positions");
        var zoned = CaptureReport.Replay("Cyl-> 1 2\nH. S: 890\n0. 0:  .X", "mac.800");
        Check(zoned.MapCylinders!.SequenceEqual(new[] { 18, 19, 20 }) && !zoned.ValidSectors()[0]
            && zoned.ValidSectors()[zoned.Layout!.SectorOffset(19, 0)], "grid headers and leading blank cells retain cylinder positions");
    }
    private static void TestDetection()
    {
        string path = Path.Combine(Work, "geometry.img");
        Fat12Image.Create("ibm.720").SaveCopy(path);
        Check(FormatGuesser.Guess(path) == "ibm.720" && FormatGuesser.Explain(path, "ibm.720").Contains("boot sector"), "DOS content detection and explanation");
        string atari = Path.Combine(Work, "single-sided.img");
        Fat12Image.Create("ibm.360").SaveCopy(atari);
        byte[] data = File.ReadAllBytes(atari); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 1); File.WriteAllBytes(atari, data);
        Check(FormatGuesser.Guess(atari) == "atarist.360", "same-sized image resolved by geometry");
        Check(FormatGuesser.WriteProblem(atari, "ibm.360") != null, "boot geometry mismatch rejected before writing");
        Check(FormatGuesser.Guess(Path.Combine(Work, "absent.img")) == null, "missing image");
    }
    private static void TestFat12()
    {
        foreach (string format in new[] { "ibm.160", "ibm.180", "ibm.320", "ibm.360", "ibm.720", "ibm.1200", "ibm.1440", "ibm.2880" })
        {
            string path = Path.Combine(Work, format + ".img");
            var image = Fat12Image.Create(format);
            if (format == "ibm.720") Check(image.Bpb.Media == 0xf9, "720 KB DOS media descriptor");
            Check(image.Entries().Count == 0 && image.FreeBytes > 0, "blank filesystem " + format);
            image.SaveCopy(path); Check(Fat12Image.CanOpen(path), "valid BPB and FAT " + format);
            Check(FormatGuesser.Guess(path) == format, "geometry detection " + format);
        }
        string original = Path.Combine(Work, "ibm.1440.img"), before = Hash(original);
        var disk = Fat12Image.Open(original);
        byte[] payload = Enumerable.Range(0, 1700).Select(n => (byte)n).ToArray();
        string source = Path.Combine(Work, "long host filename.bin"); File.WriteAllBytes(source, payload);
        string empty = Path.Combine(Work, "EMPTY.TXT"); File.WriteAllBytes(empty, []);
        disk.ImportFiles([source, empty]);
        var entries = disk.Entries();
        Check(entries.Count == 2 && disk.ReadFile(entries.First(e => e.Size == payload.Length)).SequenceEqual(payload), "multi-cluster import/extraction");
        Check(disk.ReadFile(entries.First(e => e.Size == 0)).Length == 0, "empty file");
        disk.ImportFiles([source]); Check(disk.Entries().Select(e => e.Name).Distinct().Count() == 3, "DOS alias collision handled");
        string edited = Path.Combine(Work, "edited.img"); disk.SaveCopy(edited);
        Check(Hash(original) == before, "original untouched by import and save");
        Reject(() => disk.SaveCopy(original), "cannot save over source");
        Reject(() => disk.SaveCopy(edited), "cannot overwrite existing destination");
        var reopened = Fat12Image.Open(edited);
        Check(reopened.ReadFile(reopened.Entries().First(e => e.Size == payload.Length)).SequenceEqual(payload), "saved copy reopens");
        long free = reopened.FreeBytes; int files = reopened.Entries().Count;
        string large = Path.Combine(Work, "huge.bin"); File.WriteAllBytes(large, new byte[1500000]);
        Reject(() => reopened.ImportFiles([empty, large]), "batch out-of-space rollback");
        Check(reopened.FreeBytes == free && reopened.Entries().Count == files, "failed import rolled back all files");
        byte[] corrupt = File.ReadAllBytes(edited);
        var file = reopened.Entries().First(e => e.Size > 512);
        int fatOffset = reopened.Bpb.Reserved * reopened.Bpb.BytesPerSector + file.Cluster + file.Cluster / 2;
        ushort old = BinaryPrimitives.ReadUInt16LittleEndian(corrupt.AsSpan(fatOffset));
        ushort cycle = (ushort)(file.Cluster % 2 == 0 ? old & 0xf000 | file.Cluster : old & 0xf | file.Cluster << 4);
        BinaryPrimitives.WriteUInt16LittleEndian(corrupt.AsSpan(fatOffset), cycle);
        string cyclic = Path.Combine(Work, "cycle.img"); File.WriteAllBytes(cyclic, corrupt);
        var broken = Fat12Image.Open(cyclic);
        Reject(() => broken.ReadFile(broken.Entries().First(e => e.Name == file.Name)), "cyclic file chains cannot hang");
        Reject(() => broken.ImportFiles([empty]), "mismatched FATs cannot be edited");
        corrupt[13] = 0; string bpb = Path.Combine(Work, "invalid-bpb.img"); File.WriteAllBytes(bpb, corrupt);
        Check(!Fat12Image.CanOpen(bpb), "invalid BPB rejected");
        if (Environment.GetEnvironmentVariable("HAMSTERWEAZLE_FAT12_FIXTURE") is { Length: > 0 } fixture)
        {
            var nested = Fat12Image.Open(fixture);
            Check(nested.Entries().Any(e => e.Path == "NESTED/DEEP" && e.IsDirectory), "independently created nested directories");
            var nestedFile = nested.Entries().Single(e => e.Path == "NESTED/DEEP/HELLO.TXT");
            Check(nested.ReadFile(nestedFile).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("Independent nested FAT12 fixture.\n")),
                "independently created nested file extraction");
        }
    }
    private static CaptureReport Report(string path, HashSet<int> missing)
    {
        var layout = DiskLayout.ForFormat("ibm.1440")!;
        var report = new CaptureReport { Operation = "read", Format = "ibm.1440", ImagePath = path, Layout = layout };
        for (int h = 0; h < 2; h++) for (int s = 0; s < 18; s++)
            report.Feed($"{h}.{s,2}: " + string.Concat(Enumerable.Range(0, 80).Select(c => missing.Contains((c * 2 + h) * 18 + s) ? 'X' : '.')));
        report.Finish(0, false); CaptureStore.Save(report); return report;
    }
    private static void TestRescue()
    {
        string source = Path.Combine(Work, "source.img"), pass = Path.Combine(Work, "pass.img"), combined = Path.Combine(Work, "combined.img");
        byte[] a = Enumerable.Repeat((byte)0x11, 1474560).ToArray(), b = (byte[])a.Clone();
        Array.Clear(a, 0, 512); Array.Fill(b, (byte)0x22, 0, 512); // Both are valid; earlier zeros must survive.
        Array.Fill(a, (byte)0xee, 512, 512); Array.Fill(b, (byte)0x33, 512, 512); // Old hole is recovered.
        Array.Fill(b, (byte)0xee, 1024, 512); // New hole cannot destroy earlier data.
        File.WriteAllBytes(source, a); File.WriteAllBytes(pass, b);
        var oldReport = Report(source, [1]);
        var newReport = Report(pass, [2]);
        string oldHash = Hash(source), passHash = Hash(pass);
        var result = RescueReader.Merge(source, oldReport, pass, newReport, combined);
        byte[] rescued = File.ReadAllBytes(combined);
        Check(result.Recovered == 1 && result.Remaining == 0 && result.Conflicts == 1, "rescue and conflict counts");
        Check(rescued.AsSpan(0, 512).ToArray().All(n => n == 0), "valid zero sector preserved");
        Check(rescued[512] == 0x33 && rescued[1024] == 0x11, "only missing old sectors replaced");
        Check(Hash(source) == oldHash && Hash(pass) == passHash, "both originals byte-identical");
        var saved = CaptureStore.FindForImage(combined)!;
        Check(saved.ValidSectors().All(v => v) && saved.ConflictingSectors == 1 && saved.Summary().Contains("1 conflicts"), "combined metadata survives reload");
        Check(saved.SnapshotTracks().Count == 160, "collected map covers full disk");
        string nextPass = Path.Combine(Work, "next-pass.img");
        b[5 * 512] ^= 1; File.WriteAllBytes(nextPass, b);
        var nextReport = Report(nextPass, [2]);
        string nextCombined = Path.Combine(Work, "next-combined.img");
        var nextResult = RescueReader.Merge(combined, saved, nextPass, nextReport, nextCombined);
        Check(nextResult.Conflicts == 2 && CaptureStore.FindForImage(nextCombined)!.ConflictingSectorIndices.SequenceEqual(new[] { 0, 5 }),
            "repeated rescue remembers distinct conflicts without double counting");
        string renamed = Path.Combine(Work, "renamed.img");
        File.Copy(combined, renamed); CaptureStore.FollowRename(combined, renamed);
        Check(CaptureStore.FindForImage(renamed)?.ImagePath == renamed && CaptureStore.FindForImage(renamed)?.ValidSectors().All(v => v) == true, "report and rescue validity follow renaming");
        Reject(() => RescueReader.Merge(source, oldReport, pass, newReport, combined), "rescue never overwrites output");
        a[2000] ^= 1; File.WriteAllBytes(source, a);
        Check(CaptureStore.FindForImage(source) == null, "stale reports do not match edited images");
        Reject(() => RescueReader.Merge(source, oldReport, pass, newReport, combined + ".new"), "stale hash rejected");
    }
    private static async Task TestRunner()
    {
        var runner = new GwRunner { GwPath = Environment.ProcessPath };
        string path = Path.Combine(Work, "runner capture & spaces.img");
        string command = runner.BuildArguments(GwOperation.Read, "ibm.1440", path, new(Drive: "B", DevicePort: "COM17"));
        Check(await runner.RunAsync(command) == 0, "fake read execution");
        Check(runner.LastReport?.Command == command && runner.LastReport.ImagePath == path && CaptureStore.FindForImage(path) != null, "quoted path and automatic report association");
        Check(Directory.GetFiles(Work, "runner*.txt").Any(), "human-readable report saved");
        Check(await runner.RunAsync(runner.BuildArguments(GwOperation.Write, "ibm.1440", path, new(Verify: true))) == 0 && runner.LastReport!.Verified, "actual positive verification tracked");
        string original = Path.Combine(Work, "converted-original.dc42");
        File.WriteAllBytes(original, new byte[1474644]);
        Check(await runner.RunAsync(runner.BuildArguments(GwOperation.Write, "ibm.1440", path, new(Verify: true)), original) == 0,
            "write preflight checks the converted command image");
        Check(CaptureStore.FindForImage(original, readOnly: false)?.Verified == true, "converted write report stays with the original container");
        string cancelled = Path.Combine(Work, "cancelled.img");
        var task = runner.RunAsync($"read --format ibm.1440 --slow \"{cancelled}\"");
        await Task.Delay(200); runner.Cancel();
        try { await task; throw new Exception("Cancellation did not stop the tool"); } catch (OperationCanceledException) { }
        Check(runner.LastReport?.Cancelled == true && Directory.GetFiles(Work, "cancelled*.json").Length > 0, "cancelled capture retains report");
        Check(CaptureReport.Replay(runner.LastReport!.ToText(), "ibm.720").Cancelled, "saved text replay retains cancellation metadata");
    }
}
