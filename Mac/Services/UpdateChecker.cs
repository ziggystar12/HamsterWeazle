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
                if (!string.IsNullOrEmpty(dl)) { assetUrl = dl; assetName = name; break; }
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

    public static async Task InstallHxcFromZip(string zipPath, string installDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await InstallFromZip(zipPath, installDir, "HxCFloppyEmulator.exe");
            return;
        }

        // Mac: extract outer ZIP, find the DMG inside, mount it, copy the app + CLI out
        string tmpZip = Path.Combine(Path.GetTempPath(), "hw_hxc_zip_tmp");
        if (Directory.Exists(tmpZip)) Directory.Delete(tmpZip, recursive: true);
        Directory.CreateDirectory(tmpZip);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmpZip, overwriteFiles: true));
            string? dmg = FindFileRecursive(tmpZip, "HxCFloppyEmulator.dmg");
            if (dmg == null) { await InstallFromZip(zipPath, installDir, "HxCFloppyEmulator"); return; }

            string mountPoint = await MountDmgAsync(dmg);
            try
            {
                Directory.CreateDirectory(installDir);
                // Copy GUI .app bundle
                string appSrc = Path.Combine(mountPoint, "HxCFloppyEmulator.app");
                if (Directory.Exists(appSrc))
                    CopyDirectory(appSrc, Path.Combine(installDir, "HxCFloppyEmulator.app"));
                // Copy CLI + its dylibs (preserves App/ + Frameworks/ structure hxcfe needs)
                string cliSrc = Path.Combine(mountPoint, "hxcfe_cmdline");
                if (Directory.Exists(cliSrc))
                    CopyDirectory(cliSrc, Path.Combine(installDir, "hxcfe_cmdline"));
            }
            finally { await UnmountDmgAsync(mountPoint); }
        }
        finally { try { Directory.Delete(tmpZip, recursive: true); } catch { } }
    }

    private static async Task<string> MountDmgAsync(string dmgPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            "hdiutil", $"attach \"{dmgPath}\" -nobrowse -mountrandom /tmp -noverify")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        // Last path-looking token on last line is the mount point
        foreach (string line in output.Split('\n').Reverse())
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('/')) return trimmed.Split('\t').Last().Trim();
        }
        throw new Exception($"Could not determine DMG mount point from: {output}");
    }

    private static async Task UnmountDmgAsync(string mountPoint)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("hdiutil", $"detach \"{mountPoint}\" -quiet")
            { UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static string? FindGwExe()     => FindInDirs("gw.exe",                 "greaseweazle");
    public static string? FindHxcGuiExe() => FindInDirs("HxCFloppyEmulator.exe", "hxc");

    public static string? FindHxcCliExe(string? hintDir = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !string.IsNullOrEmpty(hintDir) && Directory.Exists(hintDir))
        {
            // Recurse from the GUI install dir. Skip anything in a Windows subfolder or with .exe.
            foreach (string f in AllFilesIn(hintDir))
            {
                if (f.Contains("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(f).Equals("hxcfe", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        return FindInDirs("hxcfe.exe", "hxc");
    }

    // Search a specific directory for the HxC GUI binary (any platform name)
    public static string? FindHxcInDir(string dir)
    {
        bool isMac = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        foreach (string name in new[] { "HxCFloppyEmulator.app", "HxCFloppyEmulator",
                                        "HxCFloppyEmulator.exe", "hxcfloppyemulator" })
        {
            if (isMac && name.EndsWith(".exe")) continue;
            string p = Path.Combine(dir, name);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        // Fallback: find any executable in the dir tree
        foreach (string f in AllFilesIn(dir))
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            if (isMac && n.EndsWith(".exe")) continue;
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

    /// <summary>Cross-platform self-update launcher. Waits for this process to exit, then installs the update.</summary>
    public static void LaunchSelfUpdateScript(string updatePath, string currentExePath)
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
                string.Concat("move /y \"", updatePath, "\" \"", currentExePath, "\""),
                string.Concat("start \"\" \"", currentExePath, "\""),
            });
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", string.Concat("/c \"", batPath, "\""))
            { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                 && Path.GetExtension(updatePath).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                 && FindCurrentAppBundle(currentExePath) is { } currentApp)
        {
            string sh = Path.Combine(Path.GetTempPath(), "hw_update.sh");
            File.WriteAllLines(sh, new[]
            {
                "#!/bin/bash",
                "set -e",
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done",
                string.Concat("UPDATE_ZIP=", ShellQuote(updatePath)),
                string.Concat("CURRENT_APP=", ShellQuote(currentApp)),
                "WORK_DIR=\"$(mktemp -d /tmp/hw_update.XXXXXX)\"",
                "cleanup() { rm -rf \"$WORK_DIR\"; }",
                "trap cleanup EXIT",
                "/usr/bin/ditto -x -k \"$UPDATE_ZIP\" \"$WORK_DIR\"",
                "NEW_APP=\"$(/usr/bin/find \"$WORK_DIR\" -maxdepth 3 -type d -name 'HamsterWeazle.app' -print -quit)\"",
                "if [ -z \"$NEW_APP\" ]; then echo \"HamsterWeazle.app not found in update zip\"; exit 1; fi",
                "rm -rf \"$CURRENT_APP\"",
                "/usr/bin/ditto \"$NEW_APP\" \"$CURRENT_APP\"",
                "/usr/bin/xattr -dr com.apple.quarantine \"$CURRENT_APP\" 2>/dev/null || true",
                "/usr/bin/open \"$CURRENT_APP\"",
            });
            System.Diagnostics.Process.Start("chmod", $"+x \"{sh}\"")?.WaitForExit();
            System.Diagnostics.Process.Start("bash", sh);
        }
        else
        {
            // Mac / Linux: shell script
            string sh = Path.Combine(Path.GetTempPath(), "hw_update.sh");
            File.WriteAllLines(sh, new[]
            {
                "#!/bin/bash",
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done",
                $"mv \"{updatePath}\" \"{currentExePath}\"",
                $"chmod +x \"{currentExePath}\"",
                $"open \"{currentExePath}\"",
            });
            System.Diagnostics.Process.Start("chmod", $"+x \"{sh}\"")?.WaitForExit();
            System.Diagnostics.Process.Start("bash", sh);
        }
    }

    private static string? FindCurrentAppBundle(string currentExePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(currentExePath) ?? "");
        while (dir != null)
        {
            if (dir.Extension.Equals(".app", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string ShellQuote(string value) =>
        string.Concat("'", value.Replace("'", "'\\''"), "'");
}
