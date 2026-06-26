using System.IO;
using System.Text.Json.Serialization;

namespace HamsterWeazle.Models;

public class WriteQueueItem
{
    public string FilePath { get; set; } = "";
    public string Format { get; set; } = "";
    public string Vendor { get; set; } = "";
    public int? StartCyl { get; set; }
    public int? EndCyl { get; set; }
    public int Retries { get; set; } = 3;
    public bool Verify { get; set; }
    public string? Drive { get; set; }
    public string DriveName { get; set; } = "";
    public int? Revs { get; set; }
    public string DevicePort { get; set; } = "";
    public DateTime LastWritten { get; set; }

    [JsonIgnore] public string FileName => Path.GetFileName(FilePath);
    [JsonIgnore] public string ShortPath
    {
        get
        {
            if (FilePath.Length <= 34) return FilePath;
            return string.Concat("...", FilePath.AsSpan(FilePath.Length - 31));
        }
    }
    [JsonIgnore] public string DateLabel => LastWritten.ToString("dd MMM HH:mm");
    [JsonIgnore] public string DriveLabel => !string.IsNullOrWhiteSpace(DriveName) ? DriveName : Drive switch
    {
        "0" => "A:",
        "1" => "B:",
        "2" => "Shugart DS2",
        "3" => "Shugart DS3",
        _   => "Auto"
    };
    [JsonIgnore] public bool FileExists => File.Exists(FilePath);
}
