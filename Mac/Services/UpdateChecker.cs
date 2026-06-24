using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HamsterWeazle.Services;

public record GhRelease(string TagName, string DownloadUrl, string AssetName);

public static class UpdateChecker
{
    private static readonly HttpClient Http = new();

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
                if (!string.IsNullOrEmpty(dl)) { assetUrl = dl; assetName = name; break; }
            }
            return new GhRelease(tag, assetUrl, assetName);
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
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? InstallFromZip(zipPath, installDir, "HxCFloppyEmulator.exe")
            : InstallFromZip(zipPath, installDir, "HxCFloppyEmulator");

    public static string? FindGwExe()     => FindInDirs("gw.exe",                 "greaseweazle");
    public static string? FindHxcGuiExe() => FindInDirs("HxCFloppyEmulator.exe", "hxc");

    public static string? FindHxcCliExe(string? hintDir = null)
    {
        // Search recursively from the GUI install dir — hxcfe may be inside a .app bundle
        if (!string.IsNullOrEmpty(hintDir) && Directory.Exists(hintDir))
        {
            string? found = FindFileRecursive(hintDir, "hxcfe")
                         ?? FindFileRecursive(hintDir, "hxcfe.exe");
            if (found != null) return found;
        }
        return FindInDirs("hxcfe.exe", "hxc");
    }

    // Search a specific directory for the HxC GUI binary (any platform name)
    public static string? FindHxcInDir(string dir)
    {
        foreach (string name in new[] { "HxCFloppyEmulator", "HxCFloppyEmulator.exe",
                                        "hxcfloppyemulator", "HxCFloppyEmulator.app" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        // Fallback: find any executable in the dir tree
        foreach (string f in AllFilesIn(dir))
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            if (n.StartsWith("hxc") && (n.EndsWith(".exe") || !n.Contains('.')))
                return f;
        }
        return null;
    }

    private static string? FindInDirs(string exe, string subFolder)
    {
        // Check next to exe and in sub-folder first
        string baseDir = AppContext.BaseDirectory;
        string sub     = Path.Combine(baseDir, subFolder, exe);
        if (File.Exists(sub)) return sub;
        string same = Path.Combine(baseDir, exe);
        if (File.Exists(same)) return same;

        // On Mac/Linux, also search without the .exe extension
        var names = new List<string> { exe };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string noExt = Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrEmpty(noExt)) names.Add(noExt);
        }

        // Search PATH plus common pipx/homebrew locations the GUI app may not have
        string pathVar = string.Join(Path.PathSeparator.ToString(), new[]
        {
            Environment.GetEnvironmentVariable("PATH") ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
        });
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (string name in names)
            {
                string p = Path.Combine(dir.Trim(), name);
                if (File.Exists(p)) return p;
            }
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

    /// <summary>Cross-platform self-update launcher. Waits for this process to exit, then replaces the exe.</summary>
    public static void LaunchSelfUpdateScript(string newExePath, string currentExePath)
    {
        int pid = Environment.ProcessId;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string batPath = Path.Combine(Path.GetTempPath(), "hamsterweazle_update.bat");
            File.WriteAllLines(batPath, new[]
            {
                "@echo off",
                ":loop",
                string.Concat("tasklist 2>nul | findstr /C:\"", pid, "\" >nul && timeout /t 1 /nobreak >nul && goto loop"),
                string.Concat("move /y \"", newExePath, "\" \"", currentExePath, "\""),
                string.Concat("start \"\" \"", currentExePath, "\""),
            });
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", string.Concat("/c \"", batPath, "\""))
            { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        else
        {
            // Mac / Linux: shell script
            string sh = Path.Combine(Path.GetTempPath(), "hw_update.sh");
            File.WriteAllLines(sh, new[]
            {
                "#!/bin/bash",
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done",
                $"mv \"{newExePath}\" \"{currentExePath}\"",
                $"chmod +x \"{currentExePath}\"",
                $"open \"{currentExePath}\"",
            });
            System.Diagnostics.Process.Start("chmod", $"+x \"{sh}\"")?.WaitForExit();
            System.Diagnostics.Process.Start("bash", sh);
        }
    }
}
