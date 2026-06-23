namespace HamsterWeazle.Models;

public record DiskFormat(string Vendor, string ShortName, string FullName)
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ibm.160"]          = "160 KB (40t SS 8s)",
        ["ibm.180"]          = "180 KB (40t SS 9s)",
        ["ibm.320"]          = "320 KB (40t DS 8s)",
        ["ibm.360"]          = "360 KB (40t DS 9s)",
        ["ibm.720"]          = "720 KB DD",
        ["ibm.800"]          = "800 KB (80t DS 10s)",
        ["ibm.1200"]         = "1.2 MB HD 5.25\"",
        ["ibm.1440"]         = "1.44 MB HD",
        ["ibm.1680"]         = "1.68 MB DMF",
        ["ibm.dmf"]          = "1.68 MB DMF",
        ["ibm.2880"]         = "2.88 MB ED",
        ["ibm.scan"]         = "Scan (auto-detect)",
        ["amiga.amigados"]   = "880 KB DD",
        ["amiga.amigados-hd"]= "1.76 MB HD",
        ["commodore.1541"]   = "170 KB (1541)",
        ["commodore.1571"]   = "340 KB (1571)",
        ["commodore.1581"]   = "800 KB (1581)",
        ["atarist.360"]      = "360 KB SS",
        ["atarist.720"]      = "720 KB DS",
        ["atarist.1440"]     = "1.44 MB HD",
    };

    public string DisplayName
    {
        get
        {
            if (Descriptions.TryGetValue(FullName, out string? desc))
                return string.Concat(ShortName, "  —  ", desc);
            return ShortName;
        }
    }

    public override string ToString() => DisplayName;
}
