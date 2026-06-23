using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using HamsterWeazle.Models;

namespace HamsterWeazle.Services;

public static class CfgParser
{
    private static readonly Dictionary<string, string> _vendorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acorn"]      = "Acorn",
        ["akai"]       = "Akai",
        ["amiga"]      = "Amiga",
        ["apple2"]     = "Apple II",
        ["atari"]      = "Atari 8-bit",
        ["atarist"]    = "Atari ST",
        ["coco"]       = "CoCo / Dragon",
        ["commodore"]  = "Commodore",
        ["datageneral"]= "Data General",
        ["dec"]        = "DEC",
        ["dragon"]     = "Dragon",
        ["eagle"]      = "Eagle",
        ["ensoniq"]    = "Ensoniq",
        ["epson"]      = "Epson",
        ["gem"]        = "GEM",
        ["hp"]         = "HP",
        ["ibm"]        = "IBM PC",
        ["kaypro"]     = "Kaypro",
        ["luxor"]      = "Luxor",
        ["mac"]        = "Apple Mac",
        ["micropolis"] = "Micropolis",
        ["mm1"]        = "MM/1",
        ["msx"]        = "MSX",
        ["northstar"]  = "North Star",
        ["occ1"]       = "OCC-1",
        ["olivetti"]   = "Olivetti",
        ["pc98"]       = "NEC PC-98",
        ["raw"]        = "Raw Flux",
        ["sci"]        = "SCI",
        ["sega"]       = "Sega",
        ["thomson"]    = "Thomson",
        ["tsc"]        = "TSC",
        ["xerox"]      = "Xerox",
        ["zx"]         = "ZX Spectrum",
    };

    public static IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> Parse()
    {
        var result = new List<(string, IReadOnlyList<DiskFormat>)>();

        string? mainCfg = TryRead("diskdefs.cfg");
        if (mainCfg == null) return result;

        foreach (Match m in Regex.Matches(mainCfg, @"^import\s+(\S+)\s+""([^""]+)""", RegexOptions.Multiline))
        {
            string prefix = m.Groups[1].Value.TrimEnd('.');
            string filename = m.Groups[2].Value;

            string? content = TryRead(filename);
            if (content == null) continue;

            var formats = new List<DiskFormat>();
            foreach (Match dm in Regex.Matches(content, @"^disk\s+(\S+)", RegexOptions.Multiline))
            {
                string name = dm.Groups[1].Value;
                formats.Add(new DiskFormat(
                    Vendor:    VendorDisplayName(prefix),
                    ShortName: name,
                    FullName:  $"{prefix}.{name}"));
            }

            if (formats.Count > 0)
                result.Add((VendorDisplayName(prefix), formats));
        }

        return result;
    }

    private static string VendorDisplayName(string prefix) =>
        _vendorNames.TryGetValue(prefix, out string? name) ? name
            : char.ToUpper(prefix[0]) + prefix[1..];

    private static string? TryRead(string filename)
    {
        // Prefer a file on disk next to the exe (allows user updates)
        string sideBySide = Path.Combine(AppContext.BaseDirectory, filename);
        if (File.Exists(sideBySide))
            return File.ReadAllText(sideBySide);

        // Fall back to embedded resource
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(filename);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
