using System.Threading.Tasks;

namespace HamsterWeazle.Platform;

/// <summary>
/// Abstracts the parts of HamsterWeazle that differ between Windows and Mac/Linux.
/// Add a new method here when you discover something that needs to behave differently
/// per platform, then implement it in WindowsPlatform.cs and MacPlatform.cs.
/// </summary>
public interface IPlatform
{
    // --- gw binary ---
    string GwBinaryName { get; }           // "gw.exe" on Windows, "gw" on Mac
    bool   CanAutoDownloadGw { get; }      // Windows: yes (zip). Mac: no (use pipx)
    string GwInstallInstructions { get; }  // Shown in SetupDialog when download unavailable

    // --- gw install / update ---
    Task   InstallGwFromZip(string zipPath, string targetDir);
    string GetDefaultGwInstallDir();       // e.g. ./greaseweazle/ vs ~/bin/

    // --- HxC ---
    bool   HasHxcSupport { get; }          // false if no Mac binary exists
    string? FindHxcGuiExe();
    string? FindHxcCliExe();

    // --- Self-update ---
    void LaunchSelfUpdateScript(string newExePath, string currentExePath);

    // --- Settings location ---
    string GetSettingsDir();               // next to exe on Windows, ~/.config/ on Mac

    // --- Open folder ---
    void OpenFolder(string path);          // explorer.exe on Windows, open on Mac
}
