using System.IO;
using System.Runtime.InteropServices;
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
    public bool   HxcSetupOffered { get; set; } = false;
    public string DevicePort { get; set; } = "";
    public int Retries { get; set; } = 3;
    public bool VerifyAfterWrite { get; set; } = false;
    public List<WriteQueueItem> WriteQueueItems { get; set; } = new();
    public double WindowWidth { get; set; } = 960;
    public double WindowHeight { get; set; } = 720;
    public string SeenWhatsNewVersion { get; set; } = "";
    public bool AutoDetectDriveType { get; set; } = true;
}

public static class SettingsManager
{
    // On Mac/Linux use XDG config dir; on Windows keep next to the exe (WPF behaviour).
    private static readonly string _path = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.json")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HamsterWeazle", "settings.json");

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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
