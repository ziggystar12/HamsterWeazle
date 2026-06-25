using System.IO;

namespace HamsterWeazle.Services;

public static class FormatGuesser
{
    public static string? Guess(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        string ext  = Path.GetExtension(filePath).ToLowerInvariant();
        long   size = new FileInfo(filePath).Length;

        switch (ext)
        {
            case ".adf":
                return size > 1000000 ? "amiga.amigados_hd" : "amiga.amigados";
            case ".d64": return "commodore.1541";
            case ".d71": return "commodore.1571";
            case ".d81": return "commodore.1581";
            case ".st":
            case ".msa":
                return size switch
                {
                    368640  => "atarist.360",
                    737280  => "atarist.720",
                    1474560 => "atarist.720",
                    _       => "atarist.720",
                };
            case ".hfe":
            case ".scp":
            case ".kf":
            case ".raw":
                // Raw flux / encoded flux formats — gw handles these natively,
                // no --format flag needed (pass null and omit --format from args)
                return null;
        }

        return size switch
        {
            163840   => "ibm.160",
            184320   => "ibm.180",
            327680   => "ibm.320",
            368640   => "ibm.360",
            737280   => "ibm.720",
            819200   => "ibm.800",
            1228800  => "ibm.1200",
            1474560  => "ibm.1440",
            1720320  => "ibm.dmf",
            2949120  => "ibm.2880",
            901120   => "amiga.amigados",
            1802240  => "amiga.amigados_hd",
            _        => null,
        };
    }
}
