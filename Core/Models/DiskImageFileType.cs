namespace HamsterWeazle.Models;

public sealed record DiskImageFileType(string Name, string Extension)
{
    public string Pattern => string.Concat("*", Extension);
}

public static class DiskImageFileTypes
{
    // These are the normal single-file output containers supported by the
    // bundled GreaseWeazle host tools. KryoFlux .raw is intentionally omitted:
    // it is a multi-file track set and requires a name such as disk00.0.raw.
    public static IReadOnlyList<DiskImageFileType> SaveTypes { get; } =
    [
        new("Raw sector image", ".img"),
        new("IMA sector image", ".ima"),
        new("DSK disk image", ".dsk"),
        new("HxC HFE image", ".hfe"),
        new("SuperCard Pro flux image", ".scp"),
        new("Amiga ADF image", ".adf"),
        new("Apple II DOS image", ".do"),
        new("Apple II ProDOS image", ".po"),
        new("Commodore 1541 image", ".d64"),
        new("Commodore 1571 image", ".d71"),
        new("Commodore 1581 image", ".d81"),
        new("Atari ST image", ".st"),
        new("Atari MSA image", ".msa"),
        new("Acorn single-sided image", ".ssd"),
        new("Acorn double-sided image", ".dsd"),
        new("Extended DSK image", ".edsk"),
        new("ImageDisk image", ".imd"),
        new("MGT disk image", ".mgt"),
        new("North Star image", ".nsi"),
        new("Sega SF-7000 image", ".sf7"),
        new("XDF disk image", ".xdf"),
    ];

    // Superset reported by `gw read --help`. Some entries are input-only or
    // specialised multi-file formats, but they should remain discoverable in
    // the Write/Open picker.
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".a2r", ".adf", ".ads", ".adm", ".adl", ".ctr", ".d1m", ".d2m",
        ".d4m", ".d64", ".d71", ".d81", ".d88", ".dcp", ".dim", ".dmk",
        ".do", ".dsd", ".dsk", ".edsk", ".fd", ".fdi", ".hdm", ".hfe",
        ".ima", ".img", ".imd", ".ipf", ".mgt", ".msa", ".nfd", ".nsi",
        ".po", ".raw", ".sf7", ".scp", ".ssd", ".st", ".td0", ".xdf",
    ];

    public static string PreferredExtension(string? diskFormat)
    {
        string format = diskFormat ?? "";
        if (format.StartsWith("amiga.", StringComparison.OrdinalIgnoreCase)) return ".adf";
        if (format.Equals("apple2.appledos.140", StringComparison.OrdinalIgnoreCase)) return ".do";
        if (format.Equals("apple2.prodos.140", StringComparison.OrdinalIgnoreCase)) return ".po";
        if (format.Equals("commodore.1541", StringComparison.OrdinalIgnoreCase)) return ".d64";
        if (format.Equals("commodore.1571", StringComparison.OrdinalIgnoreCase)) return ".d71";
        if (format.Equals("commodore.1581", StringComparison.OrdinalIgnoreCase)) return ".d81";
        if (format.StartsWith("atarist.", StringComparison.OrdinalIgnoreCase)) return ".st";
        if (format.StartsWith("acorn.dfs.ss", StringComparison.OrdinalIgnoreCase)) return ".ssd";
        if (format.StartsWith("acorn.dfs.ds", StringComparison.OrdinalIgnoreCase)) return ".dsd";
        return ".img";
    }

    public static int SaveTypeIndex(string extension)
    {
        for (int i = 0; i < SaveTypes.Count; i++)
            if (SaveTypes[i].Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    public static string WindowsSaveFilter()
    {
        var entries = SaveTypes.SelectMany(type => new[]
        {
            string.Concat(type.Name, " (*", type.Extension, ")"),
            type.Pattern,
        }).ToList();
        entries.Add("All files");
        entries.Add("*.*");
        return string.Join("|", entries);
    }

    public static string WindowsOpenFilter()
    {
        var entries = new List<string>
        {
            "All supported disk images",
            string.Join(";", SupportedExtensions.Select(ext => string.Concat("*", ext))),
        };
        entries.AddRange(SaveTypes.SelectMany(type => new[]
        {
            string.Concat(type.Name, " (*", type.Extension, ")"),
            type.Pattern,
        }));
        entries.Add("All files");
        entries.Add("*.*");
        return string.Join("|", entries);
    }
}
