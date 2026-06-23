using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace HamsterWeazle.Services;

public record GhRelease(string TagName, string DownloadUrl, string AssetName);

public static class UpdateChecker
{
    private static readonly HttpClient Http = new();

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("HamsterWeazle/1.2");
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
                if (!string.IsNullOrEmpty(dl)) { assetUrl = dl; assetName = name; break; }
            }
            return new GhRelease(tag, assetUrl, assetName);
        }
        catch { return null; }
    }

    public static bool IsNewer(string latest, string current)
    {
        latest  = latest.TrimStart('v');
        current = current.TrimStart('v');
        if (Version.TryParse(latest, out var vL) && Version.TryParse(current, out var vC))
            return vL > vC;
        return string.Compare(latest, current, StringComparison.Ordinal) > 0;
    }

    public static string CurrentAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? string.Concat(v.Major, ".", v.Minor, ".", v.Build) : "0.0.0";
    }

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

    public static async Task InstallGwFromZip(string zipPath, string installDir)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), "gw_install_tmp");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmpDir, overwriteFiles: true));
        string? gwExe = FindFileRecursive(tmpDir, "gw.exe");
        string sourceDir = gwExe != null ? Path.GetDirectoryName(gwExe)! : tmpDir;
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

    public static void ExtractZip(string zipPath, string targetDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            string dest = Path.Combine(targetDir, entry.Name);
            string? dir = Path.GetDirectoryName(dest);
            if (dir != null) Directory.CreateDirectory(dir);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    public static void LaunchSelfUpdateScript(string newExePath, string currentExePath)
    {
        string batPath = Path.Combine(Path.GetTempPath(), "hamsterweazle_update.bat");
        int pid = Environment.ProcessId;
        string[] lines =
        {
            "@echo off",
            ":loop",
            string.Concat("tasklist 2>nul | findstr /C:\"", pid, "\" >nul && timeout /t 1 /nobreak >nul && goto loop"),
            string.Concat("move /y \"", newExePath, "\" \"", currentExePath, "\""),
            string.Concat("start \"\" \"", currentExePath, "\""),
        };
        File.WriteAllLines(batPath, lines);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", string.Concat("/c \"", batPath, "\""))
        { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, UseShellExecute = true };
        System.Diagnostics.Process.Start(psi);
    }
}
