using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class SettingsView : UserControl
{
    private bool _refreshing;
    public bool IsInstalling { get; private set; }
    public event EventHandler? SettingsChanged;
    public event EventHandler? InstallingChanged;

    private void SaveChanges(AppSettings settings)
    {
        SettingsManager.Save(settings);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetInstalling(bool installing)
    {
        IsInstalling = installing;
        InstallingChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? _pendingHwUrl;
    private string? _pendingGwUrl;
    private string? _pendingHxcUrl;
    private string? _pendingHxcTag;

    public SettingsView()
    {
        InitializeComponent();
        RefreshValues();
    }

    public async Task RefreshAsync()
    {
        RefreshValues();
        var s = SettingsManager.Load();
        string? gwPath = s.GwPath;
        if (!string.IsNullOrEmpty(gwPath) && File.Exists(gwPath))
        {
            string ver = await GwRunner.GetVersionAsync(gwPath);
            if (SettingsManager.Load().GwPath == gwPath)
                TxtGwInstalled.Text = string.IsNullOrEmpty(ver) ? "found" : string.Concat("installed: ", ver);
        }
    }

    private void RefreshValues()
    {
        string appVer = UpdateChecker.CurrentAppVersion();
        TxtVersion.Text     = string.Concat("Version ", appVer);
        TxtHwInstalled.Text = string.Concat("installed: v", appVer);

        var s = SettingsManager.Load();
        TxtGwPath.Text  = s.GwPath  ?? "not configured";
        TxtHxcPath.Text = s.HxcPath ?? "not installed";

        TxtGwInstalled.Text = string.IsNullOrEmpty(s.GwPath) ? "not configured" : "found";

        TxtHxcInstalled.Text = string.IsNullOrEmpty(s.HxcInstalledTag)
            ? "not installed"
            : string.Concat("installed: ", s.HxcInstalledTag);

        RefreshAppearance();
    }

    public void RefreshAppearance()
    {
        _refreshing = true;
        try
        {
            string theme = "Dark";
            if (Application.Current!.TryGetResource("Win.ThemeName", null, out var val) && val is string tn)
                theme = tn;
            RbDark.IsChecked  = theme == "Dark";
            RbAmiga.IsChecked = theme == "Amiga";
            RbLight.IsChecked = theme == "Light";
        }
        finally { _refreshing = false; }
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    private void RbTheme_Changed(object? sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        if (sender is not RadioButton { IsChecked: true } selected) return;
        string next = ReferenceEquals(selected, RbAmiga) ? "Amiga" : ReferenceEquals(selected, RbLight) ? "Light" : "Dark";
        string cur  = "Dark";
        if (Application.Current!.TryGetResource("Win.ThemeName", null, out var val) && val is string tn)
            cur = tn;
        if (next == cur) return;
        App.SwitchTheme(next);
        var s = SettingsManager.Load();
        s.Theme = next;
        SaveChanges(s);
    }

    // ── Check for updates — HamsterWeazle ───────────────────────────────────

    private async void BtnCheckHw_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckHw.IsEnabled   = false;
        TxtHwUpdateStatus.Text = "Checking...";
        BtnUpdateHw.IsVisible = false;
        _pendingHwUrl = null;
        var rel = await UpdateChecker.GetLatestAppReleaseAsync();
        if (rel == null)
        {
            TxtHwUpdateStatus.Text = "Could not reach update server.";
        }
        else
        {
            string cur = UpdateChecker.CurrentAppVersion();
            if (UpdateChecker.IsNewer(rel.TagName, cur))
            {
                if (!string.IsNullOrEmpty(rel.DownloadUrl))
                {
                    TxtHwUpdateStatus.Text = string.Concat(rel.TagName, " available");
                    _pendingHwUrl = rel.DownloadUrl;
                    BtnUpdateHw.IsVisible = true;
                }
                else
                {
                    TxtHwUpdateStatus.Text = string.Concat(rel.TagName, " available at meanhamster.com");
                }
            }
            else
            {
                TxtHwUpdateStatus.Text = "Up to date";
            }
        }
        BtnCheckHw.IsEnabled = true;
    }

    private async void BtnUpdateHw_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHwUrl)) return;
        BtnUpdateHw.IsEnabled  = false;
        TxtHwUpdateStatus.Text = "Downloading...";
        SetInstalling(true);
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "HamsterWeazle_update.zip");
            await UpdateChecker.DownloadAsync(_pendingHwUrl, tmp,
                new Progress<int>(p => TxtHwUpdateStatus.Text = string.Concat("Downloading... ", p, "%")));
            string? current = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(current))
            {
                UpdateChecker.LaunchSelfUpdateScript(tmp, current);
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
            }
        }
        catch (Exception ex)
        {
            TxtHwUpdateStatus.Text = string.Concat("Failed: ", ex.Message);
            BtnUpdateHw.IsEnabled  = true;
        }
        finally { SetInstalling(false); }
    }

    // ── Check for updates — gw ───────────────────────────────────────────────

    private async void BtnCheckGw_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckGw.IsEnabled   = false;
        TxtGwUpdateStatus.Text = "Checking...";
        var rel = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");
        if (rel == null)
        {
            TxtGwUpdateStatus.Text = "Could not reach GitHub.";
        }
        else
        {
            string cur = (TxtGwInstalled.Text ?? "").Replace("installed: ", "").TrimStart('v');
            if (string.IsNullOrEmpty(cur) || cur is "not configured" or "found")
            {
                TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available");
                _pendingGwUrl = rel.DownloadUrl;
                BtnUpdateGw.IsVisible = true;
            }
            else if (UpdateChecker.IsNewer(rel.TagName, cur))
            {
                TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available");
                _pendingGwUrl = rel.DownloadUrl;
                BtnUpdateGw.IsVisible = true;
            }
            else
            {
                TxtGwUpdateStatus.Text = "Up to date";
            }
        }
        BtnCheckGw.IsEnabled = true;
    }

    private async void BtnUpdateGw_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdateGw.IsEnabled  = false;
        TxtGwUpdateStatus.Text = "Running pipx upgrade greaseweazle...";

        string envPath = string.Join(":", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
            "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin",
            Environment.GetEnvironmentVariable("PATH") ?? "",
        });
        var psi = new ProcessStartInfo("/bin/bash",
            "-c \"pipx upgrade greaseweazle || pipx install \\\"git+https://github.com/keirf/greaseweazle@latest\\\"\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment["PATH"] = envPath;

        SetInstalling(true);
        try
        {
            var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    TxtGwUpdateStatus.Text = "Updated.";
                    BtnUpdateGw.IsVisible  = false;
                }
                else
                {
                    TxtGwUpdateStatus.Text = string.Concat("Update failed (exit ", proc.ExitCode, ").");
                    BtnUpdateGw.IsEnabled  = true;
                }
            }
        }
        catch (Exception ex)
        {
            TxtGwUpdateStatus.Text = string.Concat("Error: ", ex.Message);
            BtnUpdateGw.IsEnabled  = true;
        }
        finally { SetInstalling(false); }
    }

    // ── Check for updates — HxC ──────────────────────────────────────────────

    private async void BtnCheckHxc_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckHxc.IsEnabled   = false;
        TxtHxcUpdateStatus.Text = "Checking...";
        var s   = SettingsManager.Load();
        var rel = await UpdateChecker.GetLatestReleaseAsync("jfdelnero", "HxCFloppyEmulator");
        if (rel == null)
        {
            TxtHxcUpdateStatus.Text = "Could not reach GitHub.";
        }
        else if (string.IsNullOrEmpty(s.HxcInstalledTag))
        {
            TxtHxcUpdateStatus.Text = string.Concat(rel.TagName, " available");
            _pendingHxcUrl = rel.DownloadUrl;
            _pendingHxcTag = rel.TagName;
            BtnUpdateHxc.IsVisible = true;
        }
        else if (UpdateChecker.IsNewer(rel.TagName, s.HxcInstalledTag))
        {
            TxtHxcUpdateStatus.Text = string.Concat(rel.TagName, " available");
            _pendingHxcUrl = rel.DownloadUrl;
            _pendingHxcTag = rel.TagName;
            BtnUpdateHxc.IsVisible = true;
        }
        else
        {
            TxtHxcUpdateStatus.Text = "Up to date";
        }
        BtnCheckHxc.IsEnabled = true;
    }

    private async void BtnUpdateHxc_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHxcUrl)) return;
        BtnUpdateHxc.IsEnabled  = false;
        TxtHxcUpdateStatus.Text = "Downloading...";
        string installDir = Path.Combine(AppContext.BaseDirectory, "hxc");
        string tmp        = Path.Combine(Path.GetTempPath(), "hxc_update.zip");
        SetInstalling(true);
        try
        {
            await UpdateChecker.DownloadAsync(_pendingHxcUrl, tmp,
                new Progress<int>(p => TxtHxcUpdateStatus.Text = string.Concat("Downloading... ", p, "%")));
            TxtHxcUpdateStatus.Text = "Extracting...";
            await UpdateChecker.InstallHxcFromZip(tmp, installDir);
            try { File.Delete(tmp); } catch { }

            string hxcExe = Path.Combine(installDir, "HxCFloppyEmulator");
            var s = SettingsManager.Load();
            s.HxcPath         = hxcExe;
            s.HxcInstalledTag = _pendingHxcTag ?? "";
            SaveChanges(s);

            TxtHxcPath.Text      = hxcExe;
            TxtHxcInstalled.Text = string.Concat("installed: ", s.HxcInstalledTag);
            TxtHxcUpdateStatus.Text = "Updated.";
            BtnUpdateHxc.IsVisible  = false;
        }
        catch (Exception ex)
        {
            TxtHxcUpdateStatus.Text = string.Concat("Failed: ", ex.Message);
            BtnUpdateHxc.IsEnabled  = true;
        }
        finally { SetInstalling(false); }
    }

    // ── Installation paths ───────────────────────────────────────────────────

    private async void BtnChangeGw_Click(object? sender, RoutedEventArgs e)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Locate gw", AllowMultiple = false });
        if (files.Count > 0)
        {
            var s = SettingsManager.Load();
            s.GwPath = files[0].Path.LocalPath;
            SaveChanges(s);
            TxtGwPath.Text = s.GwPath;
        }
    }

    private async void BtnChangeHxc_Click(object? sender, RoutedEventArgs e)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = "Locate HxCFloppyEmulator", AllowMultiple = false });
        if (files.Count > 0)
        {
            var s = SettingsManager.Load();
            s.HxcPath = files[0].Path.LocalPath;
            SaveChanges(s);
            TxtHxcPath.Text = s.HxcPath;
        }
    }

    // ── Links ────────────────────────────────────────────────────────────────

    private void BtnGitHub_Click(object? sender, RoutedEventArgs e)
        => OpenUrl(UpdateChecker.HamsterWeazleProductPageUrl);
    private void BtnGwGitHub_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/keirf/greaseweazle");
    private void BtnHxcGitHub_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/jfdelnero/HxCFloppyEmulator");
    private void BtnMeanHamster_Click(object? sender, RoutedEventArgs e)
        => OpenUrl("https://meanhamster.com");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
