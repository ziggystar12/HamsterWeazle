namespace HamsterWeazle.Models;

public record DiskFormat(string Vendor, string ShortName, string FullName)
{
    public override string ToString() => ShortName;
}
