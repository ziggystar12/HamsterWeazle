namespace HamsterWeazle.Models;

public class RunPreset
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Format { get; set; } = "";
    public int? StartCyl { get; set; }
    public int? EndCyl { get; set; }
    public int Retries { get; set; } = 3;
    public bool Verify { get; set; }
    public string? Drive { get; set; }
    public string DriveName { get; set; } = "";
    public int? Revs { get; set; }
}
