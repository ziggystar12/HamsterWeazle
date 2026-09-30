using System.Text.RegularExpressions;

namespace HamsterWeazle.Services;

public sealed record DiskLayout(int Cylinders, int Heads, int[] Sectors, int BytesPerSector, bool CanMerge)
{
    public int SectorCount => Sectors.Sum();
    public long ImageSize => (long)SectorCount * BytesPerSector;
    public int TrackIndex(int cylinder, int head) => cylinder * Heads + head;
    public int SectorOffset(int cylinder, int head) => Sectors.Take(TrackIndex(cylinder, head)).Sum();

    // Only containers with a known, linear sector ordering are eligible for rescue.
    public bool AcceptsContainer(string path) => CanMerge && Path.GetExtension(path).ToLowerInvariant() is
        ".img" or ".ima" or ".adf" or ".st";

    public static DiskLayout? ForFormat(string format)
    {
        int dot = format.IndexOf('.');
        if (dot < 1) return null;
        string? definition = CfgParser.ReadDefinition("diskdefs_" + format[..dot] + ".cfg");
        if (definition == null) return null;
        var disk = Regex.Match(definition, @"^disk\s+" + Regex.Escape(format[(dot + 1)..]) +
            @"\s*\r?\n(?<body>[\s\S]*?)^end\s*$", RegexOptions.Multiline);
        if (!disk.Success) return null;
        string body = disk.Groups["body"].Value;
        int ReadInt(string key) => int.TryParse(Regex.Match(body, @"\b" + key + @"\s*=\s*(\d+)").Groups[1].Value, out int n) ? n : 0;
        int cylinders = ReadInt("cyls"), heads = ReadInt("heads");
        if (cylinders is < 1 or > 256 || heads is < 1 or > 2) return null;
        int[] sectors = new int[cylinders * heads];
        int bps = 0;
        bool mixedBps = false;
        foreach (Match track in Regex.Matches(body, @"tracks\s+(\S+)\s+(\S+)\s*\r?\n([\s\S]*?)\s+end"))
        {
            string spec = track.Groups[1].Value, codec = track.Groups[2].Value, options = track.Groups[3].Value;
            if (!int.TryParse(Regex.Match(options, @"\bsecs\s*=\s*(\d+)").Groups[1].Value, out int count)) continue;
            int size = int.TryParse(Regex.Match(options, @"\bbps\s*=\s*(\d+)").Groups[1].Value, out int value)
                ? value : codec == "c64.gcr" ? 256 : 512;
            if (bps != 0 && bps != size) mixedBps = true;
            bps = size;
            for (int c = 0; c < cylinders; c++)
                for (int h = 0; h < heads; h++)
                    if (Matches(spec, c, h) && (spec != "*" || sectors[c * heads + h] == 0)) sectors[c * heads + h] = count;
        }
        bool fixedSectors = sectors.All(n => n > 0 && n == sectors[0]);
        bool family = format is "ibm.160" or "ibm.180" or "ibm.320" or "ibm.360" or "ibm.720" or "ibm.800"
            or "ibm.1200" or "ibm.1440" or "ibm.1680" or "ibm.dmf" or "ibm.2880"
            or "amiga.amigados" or "amiga.amigados_hd" or "msx.2dd"
            || format.StartsWith("atarist.");
        return new(cylinders, heads, sectors, bps == 0 ? 512 : bps, family && fixedSectors && !mixedBps);
    }

    private static bool Matches(string spec, int cylinder, int head)
    {
        if (spec == "*") return true;
        foreach (string part in spec.Split(','))
        {
            var m = Regex.Match(part, @"^(\d+)(?:-(\d+))?(?:\.(\d+))?$");
            if (!m.Success) continue;
            int start = int.Parse(m.Groups[1].Value), end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : start;
            if (cylinder >= start && cylinder <= end && (!m.Groups[3].Success || int.Parse(m.Groups[3].Value) == head)) return true;
        }
        return false;
    }
}
