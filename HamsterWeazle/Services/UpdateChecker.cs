using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HamsterWeazle.Services;

public record GhRelease(
    string TagName,
    string DownloadUrl,
    string AssetName,
    string? PageUrl = null,
    string? Notes = null);

public static class UpdateChecker
{
    private static readonly HttpClient Http = new();
    private const string AppManifestUrl = "https://meanhamster.com/downloads/hamsterweazle/latest.json";
    public const string HamsterWeazleProductPageUrl = "https://meanhamster.com/products/hamsterweazle";

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("HamsterWeazle/0.15");
        Http.Timeout = TimeSpan.FromSeconds(15);
    }

    public static async Task<GhRelease?> GetLatestReleaseAsync(string owner, string repo)
    {
        try
        {
            string url  = string.Concat("https://api.github.com/repos/", owner, "/", repo, "/releases/latest");
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            string tag    = root.GetProperty("tag_name").GetString() ?? "";
            string assetUrl = "", assetName = "";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string dl   = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (string.IsNullOrEmpty(dl)) continue;
                // On Windows only download .exe assets to avoid picking up Mac binaries
                if (owner == "ziggystar12" && repo == "HamsterWeazle"
                    && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = dl; assetName = name; break;
            }
            return new GhRelease(tag, assetUrl, assetName);
        }
        catch { return null; }
    }

    public static async Task<GhRelease?> GetLatestAppReleaseAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(AppManifestUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("platforms", out var platforms)) return null;
            if (!platforms.TryGetProperty(GetAppPlatformKey(), out var platform)) return null;

            string tag = GetString(platform, "version");
            string url = GetString(platform, "downloadUrl");
            string assetName = GetString(platform, "assetName");
            string notes = GetString(platform, "notes");
            string pageUrl = root.TryGetProperty("pageUrl", out var page)
                ? page.GetString() ?? HamsterWeazleProductPageUrl
                : HamsterWeazleProductPageUrl;

            if (string.IsNullOrEmpty(tag)) return null;
            return new GhRelease(tag, url, assetName, pageUrl, notes);
        }
        catch { return null; }
    }

    public static bool IsNewer(string latest, string current)
    {
        latest  = latest.TrimStart('v').Replace("HxCFloppyEmulator_V", "").Replace("_", ".");
        current = current.TrimStart('v').Replace("HxCFloppyEmulator_V", "").Replace("_", ".");
        if (Version.TryParse(latest, out var vL) && Version.TryParse(current, out var vC))
            return vL > vC;
        return string.Compare(latest, current, StringComparison.Ordinal) > 0;
    }

    public static string CurrentAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? string.Concat(v.Major, ".", v.Minor, ".", v.Build) : "0.0.0";
    }

    private static string GetAppPlatformKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "mac_apple_silicon"
            : "mac_intel";
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? ""
            : "";

    public static async Task DownloadAsync(string url, string destPath, IProgress<int>? progress = null)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src  = await resp.Content.ReadAsStreamAsync();
        await using var dest = File.Create(destPath);
        var buf = new byte[81920];
        long done = 0; int read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read));
            done += read;
            if (total > 0) progress?.Report((int)(done * 100 / total));
        }
    }

    public static async Task InstallFromZip(string zipPath, string installDir, string targetExeName)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "hw_install_tmp");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true));
        string? found = FindFileRecursive(tmpDir, targetExeName);
        string sourceDir = found != null ? Path.GetDirectoryName(found)! : tmpDir;
        Directory.CreateDirectory(installDir);
        foreach (string file in AllFilesIn(sourceDir))
        {
            string rel  = Path.GetRelativePath(sourceDir, file);
            string dest = Path.Combine(installDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
    }

    public static Task InstallGwFromZip(string zipPath, string installDir) =>
        InstallFromZip(zipPath, installDir, "gw.exe");

    public static Task InstallHxcFromZip(string zipPath, string installDir) =>
        InstallFromZip(zipPath, installDir, "HxCFloppyEmulator.exe");

    public static string? FindGwExe()    => FindInDirs("gw.exe",                 "greaseweazle");
    public static string? FindHxcGuiExe() => FindInDirs("HxCFloppyEmulator.exe", "hxc");
    public static string? FindHxcCliExe() => FindInDirs("hxcfe.exe",             "hxc");

    private static string? FindInDirs(string exe, string subFolder)
    {
        string base2 = AppContext.BaseDirectory;
        string sub   = Path.Combine(base2, subFolder, exe);
        if (File.Exists(sub)) return sub;
        string same  = Path.Combine(base2, exe);
        if (File.Exists(same)) return same;
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            string p = Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindFileRecursive(string dir, string name)
    {
        foreach (string f in Directory.GetFiles(dir))
            if (string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        foreach (string d in Directory.GetDirectories(dir))
        { var r = FindFileRecursive(d, name); if (r != null) return r; }
        return null;
    }

    private static IEnumerable<string> AllFilesIn(string dir)
    {
        foreach (string f in Directory.GetFiles(dir)) yield return f;
        foreach (string d in Directory.GetDirectories(dir))
            foreach (string f in AllFilesIn(d)) yield return f;
    }

    public static void LaunchSelfUpdateScript(string newExePath, string currentExePath)
    {
        string psPath = Path.Combine(Path.GetTempPath(), "hamsterweazle_update.ps1");
        int pid = Environment.ProcessId;
        string escapedNewExe = EscapeForSingleQuotedPowerShell(newExePath);
        string escapedCurrentExe = EscapeForSingleQuotedPowerShell(currentExePath);
        string[] lines =
        {
            "$ErrorActionPreference = 'Stop'",
            string.Concat("$pidToWait = ", pid),
            string.Concat("$newExe = '", escapedNewExe, "'"),
            string.Concat("$currentExe = '", escapedCurrentExe, "'"),
            "try { Wait-Process -Id $pidToWait } catch { }",
            "Start-Sleep -Milliseconds 500",
            "$maxAttempts = 10",
            "for ($attempt = 0; $attempt -lt $maxAttempts; $attempt++) {",
            "    try {",
            "        Copy-Item -LiteralPath $newExe -Destination $currentExe -Force",
            "        Remove-Item -LiteralPath $newExe -Force -ErrorAction SilentlyContinue",
            "        Start-Process -FilePath $currentExe",
            "        break",
            "    }",
            "    catch {",
            "        if ($attempt -ge ($maxAttempts - 1)) { throw }",
            "        Start-Sleep -Seconds 1",
            "    }",
            "}",
            "Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue",
        };
        File.WriteAllLines(psPath, lines);
        var psi = new System.Diagnostics.ProcessStartInfo(
            "powershell.exe",
            string.Concat("-NoProfile -ExecutionPolicy Bypass -File \"", psPath, "\""))
        { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
    }

    private static string EscapeForSingleQuotedPowerShell(string value) =>
        value.Replace("'", "''");
}
