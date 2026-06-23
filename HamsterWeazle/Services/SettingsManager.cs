using System.IO;
using System.Text.Json;

namespace HamsterWeazle.Services;

public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string? GwPath { get; set; }
    public string LastVendor { get; set; } = "IBM";
    public string LastFormat { get; set; } = "ibm.1440";
    public string LastOutputDir { get; set; } = "";
    public int Retries { get; set; } = 3;
    public bool VerifyAfterWrite { get; set; } = false;
    public List<string> RecentFiles { get; set; } = new();
    public double WindowWidth { get; set; } = 720;
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
