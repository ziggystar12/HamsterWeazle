using System.IO;

namespace HamsterWeazle.Services;

public static class FormatGuesser
{
    public static string? Guess(string filePath, string? preferredFormat = null)
    {
        try
        {
            if (!File.Exists(filePath)) return null;

            string ext  = Path.GetExtension(filePath).ToLowerInvariant();
            long   size = new FileInfo(filePath).Length;
            string? dc42Format = GuessDiskCopy42(filePath, size);
            if (dc42Format != null) return dc42Format;
            bool looksLikeMacSectorImage = ext == ".img" && LooksLikeMacSectorImage(filePath);
            if (looksLikeMacSectorImage && size == 409600)
                return "mac.400";
            if (looksLikeMacSectorImage && size == 819200)
                return "mac.800";
            if (looksLikeMacSectorImage && PreferMac(preferredFormat))
                return preferredFormat;

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
                819200   => PreferMac(preferredFormat) ? "mac.800" : "ibm.800",
                1228800  => "ibm.1200",
                1474560  => "ibm.1440",
                1720320  => "ibm.dmf",
                2949120  => "ibm.2880",
                901120   => "amiga.amigados",
                1802240  => "amiga.amigados_hd",
                _        => null,
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static bool PreferMac(string? preferredFormat) =>
        preferredFormat?.StartsWith("mac.", StringComparison.OrdinalIgnoreCase) == true;

    private static bool LooksLikeMacSectorImage(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < 1026) return false;

            Span<byte> sig = stackalloc byte[2];
            if (stream.Read(sig) == 2 && sig[0] == 0x4c && sig[1] == 0x4b)
                return true;

            stream.Position = 1024;
            if (stream.Read(sig) == 2)
            {
                // HFS MDB signatures: "BD" for HFS, "H+" for HFS+.
                if ((sig[0] == 0x42 && sig[1] == 0x44)
                    || (sig[0] == 0x48 && sig[1] == 0x2b))
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return false;
    }

    private static string? GuessDiskCopy42(string filePath, long size)
    {
        if (size < 84) return null;

        byte[] header = new byte[84];
        using (var stream = File.OpenRead(filePath))
        {
            if (stream.Read(header, 0, header.Length) != header.Length)
                return null;
        }

        // DiskCopy 4.2 images have an 84-byte header. The private word at
        // 0x52 is 0x0100, followed by data and optional 12-byte sector tags.
        if (header[0x52] != 0x01 || header[0x53] != 0x00)
            return null;

        uint dataSize = ReadUInt32BE(header, 0x40);
        uint tagSize  = ReadUInt32BE(header, 0x44);
        if (84L + dataSize + tagSize > size)
            return null;

        return dataSize switch
        {
            409600 => "mac.400",
            819200 => "mac.800",
            1474560 => "ibm.1440",
            _ => null
        };
    }

    public static bool TryCreateRawImageFromDiskCopy42(
        string filePath,
        out string rawPath,
        out string? detectedFormat)
    {
        rawPath = "";
        detectedFormat = null;

        bool completed = false;

        try
        {
            if (!TryGetDiskCopy42Info(filePath, out uint dataSize, out _, out detectedFormat))
                return false;

            rawPath = Path.Combine(
                Path.GetTempPath(),
                string.Concat("HamsterWeazle_", Guid.NewGuid().ToString("N"), ".img"));

            using var input = File.OpenRead(filePath);
            using var output = File.Create(rawPath);
            input.Position = 84;

            byte[] buffer = new byte[64 * 1024];
            uint remaining = dataSize;
            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, wanted);
                if (read <= 0)
                    return false;
                output.Write(buffer, 0, read);
                remaining -= (uint)read;
            }

            completed = true;
            return true;
        }
        catch (IOException)
        {
            if (!string.IsNullOrEmpty(rawPath))
                try { File.Delete(rawPath); } catch { }
            rawPath = "";
            detectedFormat = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            if (!string.IsNullOrEmpty(rawPath))
                try { File.Delete(rawPath); } catch { }
            rawPath = "";
            detectedFormat = null;
            return false;
        }
        finally
        {
            if (!completed && !string.IsNullOrEmpty(rawPath))
            {
                try { File.Delete(rawPath); } catch { }
                rawPath = "";
            }
        }
    }

    private static bool TryGetDiskCopy42Info(
        string filePath,
        out uint dataSize,
        out uint tagSize,
        out string? detectedFormat)
    {
        dataSize = 0;
        tagSize = 0;
        detectedFormat = null;

        try
        {
            if (!File.Exists(filePath)) return false;
            long size = new FileInfo(filePath).Length;
            if (size < 84) return false;

            byte[] header = new byte[84];
            using (var stream = File.OpenRead(filePath))
            {
                if (stream.Read(header, 0, header.Length) != header.Length)
                    return false;
            }

            if (header[0x52] != 0x01 || header[0x53] != 0x00)
                return false;

            dataSize = ReadUInt32BE(header, 0x40);
            tagSize  = ReadUInt32BE(header, 0x44);
            if (84L + dataSize + tagSize > size)
                return false;

            detectedFormat = dataSize switch
            {
                409600 => "mac.400",
                819200 => "mac.800",
                1474560 => "ibm.1440",
                _ => null
            };

            return detectedFormat != null;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24)
        | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8)
        | data[offset + 3];
}
