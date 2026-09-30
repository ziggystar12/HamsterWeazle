using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace HamsterWeazle.Services;

public sealed record Fat12Bpb(int BytesPerSector, int SectorsPerCluster, int Reserved, int Fats,
    int RootEntries, int TotalSectors, int SectorsPerFat, int SectorsPerTrack, int Heads, byte Media)
{
    public int RootSectors => (RootEntries * 32 + BytesPerSector - 1) / BytesPerSector;
    public int RootStart => Reserved + Fats * SectorsPerFat;
    public int DataStart => RootStart + RootSectors;
    public int Clusters => (TotalSectors - DataStart) / SectorsPerCluster;
    public int ClusterBytes => SectorsPerCluster * BytesPerSector;
    public static Fat12Bpb? Read(ReadOnlySpan<byte> header, long imageLength)
    {
        if (header.Length < 512) return null;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(header[11..]);
        int spc = header[13], reserved = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]), fats = header[16];
        int roots = BinaryPrimitives.ReadUInt16LittleEndian(header[17..]), total = BinaryPrimitives.ReadUInt16LittleEndian(header[19..]);
        uint longTotal = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        if (total == 0 && longTotal <= int.MaxValue) total = (int)longTotal;
        int spf = BinaryPrimitives.ReadUInt16LittleEndian(header[22..]);
        int spt = BinaryPrimitives.ReadUInt16LittleEndian(header[24..]), heads = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        if (bps is not (512 or 1024 or 2048 or 4096) || spc is < 1 or > 128 || (spc & (spc - 1)) != 0
            || reserved is < 1 or > 32 || fats is < 1 or > 2 || roots is < 1 or > 4096 || total < 1 || spf is < 1 or > 128
            || spt is < 1 or > 64 || heads is < 1 or > 2 || header[21] < 0xf0 || (long)total * bps != imageLength) return null;
        var bpb = new Fat12Bpb(bps, spc, reserved, fats, roots, total, spf, spt, heads, header[21]);
        if (bpb.DataStart >= total || bpb.Clusters is < 1 or >= 4085 || (long)spf * bps * 2 / 3 < bpb.Clusters + 2) return null;
        return bpb;
    }
}

public sealed record Fat12Entry(string Path, string Name, bool IsDirectory, int Size, int Cluster, int DirectoryOffset)
{
    public override string ToString() => IsDirectory ? Path + "/" : $"{Path}    {Size:N0} bytes";
}

/// <summary>In-memory DOS floppy editing. Saving always creates a separate image.</summary>
public sealed class Fat12Image
{
    private byte[] _data;
    public string? SourcePath { get; }
    public Fat12Bpb Bpb { get; }
    public string Label => Encoding.ASCII.GetString(_data, 43, 11).Trim();
    public long FreeBytes => Enumerable.Range(2, Bpb.Clusters).Count(c => Fat(c) == 0) * (long)Bpb.ClusterBytes;
    public bool Modified { get; private set; }

    private Fat12Image(byte[] bytes, string? source)
    {
        Bpb = Fat12Bpb.Read(bytes, bytes.LongLength) ?? throw new InvalidDataException("This image is not a supported FAT12 floppy.");
        _data = bytes; SourcePath = source;
        int fatStart = Bpb.Reserved * Bpb.BytesPerSector;
        if (_data[fatStart] != Bpb.Media || _data[fatStart + 1] != 0xff || _data[fatStart + 2] != 0xff)
            throw new InvalidDataException("The FAT header does not match the boot sector.");
    }

    public static Fat12Image Open(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("This is larger than a FAT12 floppy image.");
        return new(File.ReadAllBytes(path), System.IO.Path.GetFullPath(path));
    }

    public static bool CanOpen(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            byte[] header = new byte[512];
            if (input.Read(header) != header.Length) return false;
            var bpb = Fat12Bpb.Read(header, input.Length);
            if (bpb == null) return false;
            input.Position = bpb.Reserved * bpb.BytesPerSector;
            return input.ReadByte() == bpb.Media && input.ReadByte() == 0xff && input.ReadByte() == 0xff;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<Fat12Entry> Entries()
    {
        var entries = new List<Fat12Entry>();
        var visited = new HashSet<int>();
        void Walk(int cluster, string parent, int depth)
        {
            if (depth > 64 || (cluster != 0 && !visited.Add(cluster))) throw new InvalidDataException("The directory contains a cycle or excessive nesting.");
            foreach (int offset in DirectoryOffsets(cluster))
            {
                byte first = _data[offset], attr = _data[offset + 11];
                if (first == 0) break;
                if (first == 0xe5 || attr == 0x0f || (attr & 8) != 0) continue;
                string name = Encoding.ASCII.GetString(_data, offset, 8).TrimEnd();
                string ext = Encoding.ASCII.GetString(_data, offset + 8, 3).TrimEnd();
                if (name is "." or "..") continue;
                if (ext.Length > 0) name += "." + ext;
                if (name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) throw new InvalidDataException("A directory entry contains an invalid filename.");
                int start = U16(offset + 26);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 28));
                if (length > int.MaxValue) throw new InvalidDataException("A file length is invalid.");
                var entry = new Fat12Entry(parent + name, name, (attr & 16) != 0, (int)length, start, offset);
                entries.Add(entry);
                if (entry.IsDirectory) { if (start < 2) throw new InvalidDataException("A directory cluster is invalid."); Walk(start, entry.Path + "/", depth + 1); }
            }
        }
        Walk(0, "", 0);
        return entries;
    }

    public byte[] ReadFile(Fat12Entry entry)
    {
        if (entry.IsDirectory) throw new InvalidOperationException("Select a file to extract.");
        if (!Entries().Any(e => e == entry)) throw new InvalidDataException("The selected entry is not part of this image.");
        if (entry.Size > (long)Bpb.Clusters * Bpb.ClusterBytes) throw new InvalidDataException("File size exceeds the disk capacity.");
        byte[] output = new byte[entry.Size];
        int position = 0;
        foreach (int cluster in Chain(entry.Cluster))
        {
            int count = Math.Min(Bpb.ClusterBytes, output.Length - position);
            _data.AsSpan(ClusterOffset(cluster), count).CopyTo(output.AsSpan(position)); position += count;
            if (position == output.Length) return output;
        }
        if (position != output.Length) throw new InvalidDataException("The file has an incomplete cluster chain.");
        return output;
    }

    public IReadOnlyList<string> ImportFiles(IEnumerable<string> paths)
    {
        int length = Bpb.SectorsPerFat * Bpb.BytesPerSector, first = Bpb.Reserved * Bpb.BytesPerSector;
        for (int copy = 1; copy < Bpb.Fats; copy++)
            if (!_data.AsSpan(first, length).SequenceEqual(_data.AsSpan(first + copy * length, length)))
                throw new InvalidDataException("FAT copies differ. Browse or extract files, but do not edit this image.");
        byte[] before = (byte[])_data.Clone();
        var imported = new List<string>();
        try
        {
            foreach (string path in paths)
            {
                if (new FileInfo(path).Length > FreeBytes) throw new IOException("There is not enough free space for " + System.IO.Path.GetFileName(path) + ".");
                byte[] payload = File.ReadAllBytes(path);
                var names = Entries().Where(e => !e.Path.Contains('/')).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string shortName = ShortName(System.IO.Path.GetFileName(path), names);
                int offset = DirectoryOffsets(0).FirstOrDefault(o => _data[o] is 0 or 0xe5, -1);
                if (offset < 0) throw new IOException("The root directory is full.");
                int count = (payload.Length + Bpb.ClusterBytes - 1) / Bpb.ClusterBytes;
                int[] clusters = Enumerable.Range(2, Bpb.Clusters).Where(c => Fat(c) == 0).Take(count).ToArray();
                if (clusters.Length != count) throw new IOException("There is not enough free space for " + shortName + ".");
                for (int i = 0; i < clusters.Length; i++)
                {
                    SetFat(clusters[i], i + 1 == clusters.Length ? 0xfff : clusters[i + 1]);
                    int start = ClusterOffset(clusters[i]); Array.Clear(_data, start, Bpb.ClusterBytes);
                    int source = i * Bpb.ClusterBytes;
                    payload.AsSpan(source, Math.Min(Bpb.ClusterBytes, payload.Length - source)).CopyTo(_data.AsSpan(start));
                }
                Array.Clear(_data, offset, 32);
                string stem = System.IO.Path.GetFileNameWithoutExtension(shortName), ext = System.IO.Path.GetExtension(shortName).TrimStart('.');
                Encoding.ASCII.GetBytes(stem.PadRight(8) + ext.PadRight(3)).CopyTo(_data, offset);
                _data[offset + 11] = 0x20;
                Put16(offset + 26, clusters.Length == 0 ? 0 : clusters[0]);
                BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(offset + 28), (uint)payload.Length);
                var now = DateTime.Now;
                Put16(offset + 22, now.Hour << 11 | now.Minute << 5 | now.Second / 2);
                Put16(offset + 24, Math.Clamp(now.Year - 1980, 0, 127) << 9 | now.Month << 5 | now.Day);
                imported.Add(shortName);
            }
            Modified = true;
            return imported;
        }
        catch { _data = before; throw; }
    }

    public void SaveCopy(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (SourcePath != null && string.Equals(full, SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Choose a different filename. The original image is kept unchanged.");
        using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        file.Write(_data);
        Modified = false;
    }

    public static Fat12Image Create(string format, string label = "HAMSTER")
    {
        var layout = DiskLayout.ForFormat(format);
        if (layout == null || !format.StartsWith("ibm.") || layout.BytesPerSector != 512 || !layout.CanMerge)
            throw new InvalidOperationException("Choose a standard IBM PC floppy format.");
        int sectors = layout.SectorCount;
        int spc = format is "ibm.320" or "ibm.360" or "ibm.720" or "ibm.2880" ? 2 : 1;
        int roots = format is "ibm.160" or "ibm.180" ? 64 : format == "ibm.2880" ? 240 : sectors <= 1440 ? 112 : 224;
        int rootSectors = (roots * 32 + 511) / 512, spf = 1;
        while (spf * 512 * 2 / 3 < (sectors - 1 - 2 * spf - rootSectors) / spc + 2) spf++;
        byte media = format switch { "ibm.160" => 0xfe, "ibm.180" => 0xfc, "ibm.320" => 0xff, "ibm.360" => 0xfd, "ibm.720" or "ibm.1200" => 0xf9, _ => 0xf0 };
        byte[] bytes = new byte[sectors * 512];
        void Put(int o, int n) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(o), (ushort)n);
        bytes[0] = 0xeb; bytes[1] = 0x3c; bytes[2] = 0x90;
        Encoding.ASCII.GetBytes("HMWZL1.0").CopyTo(bytes, 3);
        Put(11, 512); bytes[13] = (byte)spc; Put(14, 1); bytes[16] = 2; Put(17, roots); Put(19, sectors);
        bytes[21] = media; Put(22, spf); Put(24, layout.Sectors[0]); Put(26, layout.Heads);
        bytes[38] = 0x29; Random.Shared.NextBytes(bytes.AsSpan(39, 4));
        label = Regex.Replace(label.ToUpperInvariant(), @"[^A-Z0-9 _-]", "_");
        label = label[..Math.Min(11, label.Length)];
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(bytes, 43);
        Encoding.ASCII.GetBytes("FAT12   ").CopyTo(bytes, 54);
        // A non-bootable data disk: stop safely if booted accidentally.
        bytes[62] = 0xfa; bytes[63] = 0xf4; bytes[64] = 0xeb; bytes[65] = 0xfd;
        bytes[510] = 0x55; bytes[511] = 0xaa;
        for (int copy = 0; copy < 2; copy++) { int start = (1 + copy * spf) * 512; bytes[start] = media; bytes[start + 1] = bytes[start + 2] = 0xff; }
        int rootStart = (1 + 2 * spf) * 512;
        Encoding.ASCII.GetBytes(label.PadRight(11)).CopyTo(bytes, rootStart); bytes[rootStart + 11] = 8;
        return new(bytes, null);
    }

    private IEnumerable<int> DirectoryOffsets(int cluster)
    {
        if (cluster == 0) { for (int i = 0; i < Bpb.RootEntries; i++) yield return Bpb.RootStart * Bpb.BytesPerSector + i * 32; }
        else foreach (int c in Chain(cluster)) for (int i = 0; i < Bpb.ClusterBytes; i += 32) yield return ClusterOffset(c) + i;
    }
    private IEnumerable<int> Chain(int first)
    {
        if (first == 0) yield break;
        var seen = new HashSet<int>(); int cluster = first;
        while (true)
        {
            if (cluster < 2 || cluster >= Bpb.Clusters + 2 || !seen.Add(cluster)) throw new InvalidDataException("A cluster chain is invalid or contains a cycle.");
            yield return cluster;
            int next = Fat(cluster);
            if (next >= 0xff8) yield break;
            if (next == 0xff7 || next == 0 || next == 1) throw new InvalidDataException("A file references a bad or free cluster.");
            cluster = next;
        }
    }
    private int ClusterOffset(int cluster) => (Bpb.DataStart + (cluster - 2) * Bpb.SectorsPerCluster) * Bpb.BytesPerSector;
    private int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));
    private void Put16(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(offset), (ushort)value);
    private int Fat(int cluster)
    {
        int offset = Bpb.Reserved * Bpb.BytesPerSector + cluster + cluster / 2;
        int value = U16(offset); return cluster % 2 == 0 ? value & 0xfff : value >> 4;
    }
    private void SetFat(int cluster, int value)
    {
        for (int copy = 0; copy < Bpb.Fats; copy++)
        {
            int offset = (Bpb.Reserved + copy * Bpb.SectorsPerFat) * Bpb.BytesPerSector + cluster + cluster / 2;
            int previous = U16(offset); Put16(offset, cluster % 2 == 0 ? previous & 0xf000 | value : previous & 0x000f | value << 4);
        }
    }
    private static string ShortName(string name, HashSet<string> taken)
    {
        string Clean(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9!#$%&'()\-@^_`{}~]", "_");
        string stem = Clean(System.IO.Path.GetFileNameWithoutExtension(name)), ext = Clean(System.IO.Path.GetExtension(name).TrimStart('.'));
        if (stem.Length == 0) stem = "FILE";
        ext = ext[..Math.Min(3, ext.Length)];
        string Join(string s) => s + (ext.Length == 0 ? "" : "." + ext);
        string direct = Join(stem[..Math.Min(8, stem.Length)]);
        if (!taken.Contains(direct)) return direct;
        for (int n = 1; n < 10000; n++)
        {
            string suffix = "~" + n;
            string candidate = Join(stem[..Math.Min(8 - suffix.Length, stem.Length)] + suffix);
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new IOException("No free DOS filename is available.");
    }
}
