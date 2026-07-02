using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class SetupDialog : Window
{
    public string? GwExePath { get; private set; }

    private readonly StringBuilder _gwBuf  = new();
    private readonly StringBuilder _hxcBuf = new();

    // PATH that includes Homebrew and pipx locations GUI apps may not inherit
    private static string BuildEnvPath() => string.Join(":", new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
        "/opt/homebrew/bin",
        "/opt/homebrew/sbin",
        "/usr/local/bin",
        Environment.GetEnvironmentVariable("PATH") ?? "",
    });

    public SetupDialog() : this(false)
    {
    }

    public SetupDialog(bool hxcOnly)
    {
        InitializeComponent();
        if (hxcOnly) ShowHxcStep();
    }

    // ── Step 1: install gw ─────────────────────────────────────────────────────

    private async void BtnInstall_Click(object? sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        BtnBrowse.IsEnabled  = false;
        PanelProgress.IsVisible = true;

        string envPath = BuildEnvPath();
        bool hasBrew = FindInPath("brew", envPath) != null;
        bool hasPipx = FindInPath("pipx", envPath) != null;

        AppendGw(hasBrew ? "brew found." : hasPipx ? "pipx found (no brew needed)." : "Neither brew nor pipx found — will install Homebrew first.");

        // Write a self-contained install script
        string scriptPath = Path.Combine(Path.GetTempPath(), "hw_gw_install.sh");
        File.WriteAllText(scriptPath, BuildGwInstallScript());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        AppendGw("Running install script...\n");

        var psi = new ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment["PATH"] = envPath;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex)
        {
            AppendGw($"Failed to start shell: {ex.Message}");
            BtnInstall.IsEnabled = BtnBrowse.IsEnabled = true;
            return;
        }

        if (proc == null)
        {
            AppendGw("Could not launch installer process.");
            BtnInstall.IsEnabled = BtnBrowse.IsEnabled = true;
            return;
        }

        proc.OutputDataReceived += (_, a) => { if (a.Data != null) Dispatcher.UIThread.Post(() => AppendGw(a.Data)); };
        proc.ErrorDataReceived  += (_, a) => { if (a.Data != null) Dispatcher.UIThread.Post(() => AppendGw(a.Data)); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();

        try { File.Delete(scriptPath); } catch { }

        if (proc.ExitCode == 0)
        {
            AppendGw("\nSearching for gw...");
            GwExePath = UpdateChecker.FindGwExe();
            if (GwExePath != null)
            {
                AppendGw($"Found: {GwExePath}");
                await Task.Delay(800);
                ShowHxcStep();
            }
            else
            {
                AppendGw("gw not found in PATH — you may need to restart the app.");
                BtnBrowse.IsEnabled = true;
            }
        }
        else
        {
            AppendGw($"\nInstallation failed (exit {proc.ExitCode}). Check output above.");
            BtnInstall.IsEnabled = BtnBrowse.IsEnabled = true;
        }
    }

    private static string BuildGwInstallScript() => """
        #!/bin/bash
        set -e

        # Ensure Homebrew bin is in PATH for this session
        for p in /opt/homebrew/bin /usr/local/bin; do
            [ -d "$p" ] && export PATH="$p:$PATH"
        done

        # Install Homebrew if needed
        if ! command -v brew &>/dev/null; then
            echo "Homebrew not found. Installing Homebrew..."
            NONINTERACTIVE=1 /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
            # Re-init brew path after install
            if [ -f /opt/homebrew/bin/brew ]; then
                eval "$(/opt/homebrew/bin/brew shellenv)"
            elif [ -f /usr/local/bin/brew ]; then
                eval "$(/usr/local/bin/brew shellenv)"
            fi
            echo "Homebrew installed."
        fi

        # Install pipx if needed
        if ! command -v pipx &>/dev/null; then
            echo "Installing pipx..."
            brew install pipx
            echo "pipx installed."
        fi

        # Install greaseweazle
        if command -v gw &>/dev/null || command -v gw.exe &>/dev/null; then
            echo "gw already installed — upgrading..."
            pipx upgrade greaseweazle || true
        else
            echo "Installing GreaseWeazle host tools..."
            pipx install "git+https://github.com/keirf/greaseweazle@latest"
        fi

        echo "Done."
        """;

    private void AppendGw(string line)
    {
        _gwBuf.AppendLine(line);
        TxtProgress.Text = _gwBuf.ToString();
        ScrollProgress.ScrollToEnd();
    }

    // ── Step 2: offer HxC ─────────────────────────────────────────────────────

    private void ShowHxcStep()
    {
        PanelGw.IsVisible  = false;
        PanelHxc.IsVisible = true;
    }

    private async void BtnInstallHxc_Click(object? sender, RoutedEventArgs e)
    {
        BtnInstallHxc.IsEnabled = false;
        BtnSkipHxc.IsEnabled    = false;
        PanelHxcProgress.IsVisible = true;

        AppendHxc("Fetching latest HxCFloppyEmulator release...");

        var rel = await UpdateChecker.GetLatestReleaseAsync("jfdelnero", "HxCFloppyEmulator");
        if (rel == null || string.IsNullOrEmpty(rel.DownloadUrl))
        {
            AppendHxc("Could not reach GitHub. Set HxC path manually from Settings.");
            BtnSkipHxc.IsEnabled = true;
            return;
        }

        AppendHxc($"Downloading {rel.TagName} ({rel.AssetName})...");

        string installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications", "HxCFloppyEmulator");
        string tmp = Path.Combine(Path.GetTempPath(), "hxc_setup.zip");

        try
        {
            await UpdateChecker.DownloadAsync(rel.DownloadUrl, tmp,
                new Progress<int>(p => Dispatcher.UIThread.Post(
                    () => { TxtHxcProgress.Text = _hxcBuf + $"Downloading... {p}%"; })));

            AppendHxc("Extracting...");
            await UpdateChecker.InstallHxcFromZip(tmp, installDir);
            try { File.Delete(tmp); } catch { }

            string? hxcExe = UpdateChecker.FindHxcInDir(installDir);
            if (hxcExe != null)
            {
                var s = SettingsManager.Load();
                s.HxcPath         = hxcExe;
                s.HxcInstalledTag = rel.TagName;
                SettingsManager.Save(s);
                AppendHxc($"Installed: {hxcExe}");
                await Task.Delay(800);
                Close();
            }
            else
            {
                AppendHxc("Installed but binary not found — set path manually in Settings.");
                BtnSkipHxc.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            AppendHxc($"Failed: {ex.Message}");
            BtnSkipHxc.IsEnabled = BtnInstallHxc.IsEnabled = true;
        }
    }

    private void AppendHxc(string line)
    {
        _hxcBuf.AppendLine(line);
        TxtHxcProgress.Text = _hxcBuf.ToString();
        ScrollHxcProgress.ScrollToEnd();
    }

    private void BtnSkipHxc_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Browse / Skip ──────────────────────────────────────────────────────────

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var opts  = new FilePickerOpenOptions { Title = "Locate gw (GreaseWeazle host tools)", AllowMultiple = false };
        var files = await StorageProvider.OpenFilePickerAsync(opts);
        if (files.Count > 0)
        {
            GwExePath = files[0].Path.LocalPath;
            ShowHxcStep();
        }
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string? FindInPath(string name, string pathVar)
    {
        foreach (string dir in pathVar.Split(':'))
        {
            string full = Path.Combine(dir.Trim(), name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
