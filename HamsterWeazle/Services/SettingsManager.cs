using System.IO;
using System.Text.Json;
using HamsterWeazle.Models;

namespace HamsterWeazle.Services;

public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string? GwPath { get; set; }
    public string LastVendor { get; set; } = "IBM PC";
    public string LastFormat { get; set; } = "ibm.1440";
    public string LastOutputDir { get; set; } = "";
    public string InboxDir { get; set; } = "";
    public string LastOp { get; set; } = "Read";
    public string LastFilePath { get; set; } = "";
    public string? HxcPath { get; set; }
    public string HxcInstalledTag { get; set; } = "";
    public int Retries { get; set; } = 3;
    public bool VerifyAfterWrite { get; set; } = false;
    public List<WriteQueueItem> WriteQueueItems { get; set; } = new();
    public double WindowWidth { get; set; } = 960;
    public double WindowHeight { get; set; } = 720;
}

public static class SettingsManager
{
    private static readonly string _path = Path.Combine(
        AppContext.BaseDirectory, "HamsterWeazle.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
