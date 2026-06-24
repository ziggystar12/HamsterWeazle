using System.IO;
using System.Text.Json.Serialization;

namespace HamsterWeazle.Models;

public class WriteQueueItem
{
    public string FilePath { get; set; } = "";
    public string Format { get; set; } = "";
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
    [JsonIgnore] public bool FileExists => File.Exists(FilePath);
}
