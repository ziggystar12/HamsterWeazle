using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HamsterWeazle.Services;
using Microsoft.Win32;

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
    private string? _pendingDmkUrl;
    private string? _pendingDmkTag;
    private static readonly string Wc = ((char)42).ToString();
    private static readonly string ExeFilter = string.Concat("Executables|", Wc, ".exe");

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

        TxtGwPath.Text      = s.GwPath ?? "not configured";
        TxtHxcPath.Text     = s.HxcPath ?? "not installed";
        TxtDmkInstalled.Text = string.IsNullOrEmpty(s.DmkInstalledTag) ? "not installed" : string.Concat("installed: ", s.DmkInstalledTag);

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
            string theme = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
            RbDark.IsChecked  = theme == "Dark";
            RbAmiga.IsChecked = theme == "Amiga";
            RbLight.IsChecked = theme == "Light";
        }
        finally { _refreshing = false; }
    }

    private void RbTheme_Checked(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        if (sender is not RadioButton { IsChecked: true } selected) return;
        string next = ReferenceEquals(selected, RbAmiga) ? "Amiga" : ReferenceEquals(selected, RbLight) ? "Light" : "Dark";
        string cur  = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
        if (next == cur) return;
        App.SwitchTheme(next);
        var s = SettingsManager.Load();
        s.Theme = next;
        SaveChanges(s);
    }

    private void BtnGitHub_Click(object sender, RoutedEventArgs e)
    { OpenUrl(UpdateChecker.HamsterWeazleProductPageUrl); }

    private void BtnGwGitHub_Click(object sender, RoutedEventArgs e)
    { OpenUrl("https://github.com/keirf/greaseweazle"); }

    private void BtnHxcGitHub_Click(object sender, RoutedEventArgs e)
    { OpenUrl("https://github.com/jfdelnero/HxCFloppyEmulator"); }

    private void BtnMeanHamster_Click(object sender, RoutedEventArgs e)
    { OpenUrl("https://meanhamster.com"); }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private async void BtnCheckHw_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckHw.IsEnabled   = false;
        TxtHwUpdateStatus.Text = "Checking...";
        BtnUpdateHw.Visibility = Visibility.Collapsed;
        _pendingHwUrl = null;
        var rel = await UpdateChecker.GetLatestAppReleaseAsync();
        if (rel == null)
        { TxtHwUpdateStatus.Text = "Could not reach update server"; }
        else
        {
            string cur = UpdateChecker.CurrentAppVersion();
            if (UpdateChecker.IsNewer(rel.TagName, cur))
            {
                if (!string.IsNullOrEmpty(rel.DownloadUrl))
                {
                    TxtHwUpdateStatus.Text = string.Concat(rel.TagName, " available");
                    _pendingHwUrl = rel.DownloadUrl;
                    BtnUpdateHw.Visibility = Visibility.Visible;
                }
                else
                {
                    TxtHwUpdateStatus.Text = string.Concat(rel.TagName, " available at meanhamster.com");
                }
            }
            else
            { TxtHwUpdateStatus.Text = "Up to date"; }
        }
        BtnCheckHw.IsEnabled = true;
    }

    private async void BtnCheckGw_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckGw.IsEnabled   = false;
        TxtGwUpdateStatus.Text = "Checking...";
        var s   = SettingsManager.Load();
        var rel = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");
        if (rel == null)
        { TxtGwUpdateStatus.Text = "Could not reach GitHub"; }
        else
        {
            string cur = TxtGwInstalled.Text.Replace("installed: ", "").TrimStart('v');
            if (string.IsNullOrEmpty(cur) || cur == "not configured" || cur == "found")
            { TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available"); _pendingGwUrl = rel.DownloadUrl; BtnUpdateGw.Visibility = Visibility.Visible; }
            else if (UpdateChecker.IsNewer(rel.TagName, cur))
            { TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available"); _pendingGwUrl = rel.DownloadUrl; BtnUpdateGw.Visibility = Visibility.Visible; }
            else
            { TxtGwUpdateStatus.Text = "Up to date"; }
        }
        BtnCheckGw.IsEnabled = true;
    }

    private async void BtnCheckHxc_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckHxc.IsEnabled   = false;
        TxtHxcUpdateStatus.Text = "Checking...";
        var s   = SettingsManager.Load();
        var rel = await UpdateChecker.GetLatestReleaseAsync("jfdelnero", "HxCFloppyEmulator");
        if (rel == null)
        { TxtHxcUpdateStatus.Text = "Could not reach GitHub"; }
        else if (string.IsNullOrEmpty(s.HxcInstalledTag))
        { TxtHxcUpdateStatus.Text = string.Concat(rel.TagName, " available"); _pendingHxcUrl = rel.DownloadUrl; _pendingHxcTag = rel.TagName; BtnUpdateHxc.Visibility = Visibility.Visible; }
        else if (UpdateChecker.IsNewer(rel.TagName, s.HxcInstalledTag))
        { TxtHxcUpdateStatus.Text = string.Concat(rel.TagName, " available"); _pendingHxcUrl = rel.DownloadUrl; _pendingHxcTag = rel.TagName; BtnUpdateHxc.Visibility = Visibility.Visible; }
        else
        { TxtHxcUpdateStatus.Text = "Up to date"; }
        BtnCheckHxc.IsEnabled = true;
    }

    private async void BtnCheckDmk_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckDmk.IsEnabled = false; TxtDmkUpdateStatus.Text = "Checking..."; BtnUpdateDmk.Visibility = Visibility.Collapsed;
        var s = SettingsManager.Load(); var rel = await UpdateChecker.GetLatestDmkReleaseAsync();
        if (rel == null) TxtDmkUpdateStatus.Text = "Could not reach GitHub";
        else if (string.IsNullOrEmpty(s.DmkInstalledTag) || UpdateChecker.IsNewer(rel.TagName, s.DmkInstalledTag))
        { TxtDmkUpdateStatus.Text = string.Concat(rel.TagName, " available"); _pendingDmkUrl = rel.DownloadUrl; _pendingDmkTag = rel.TagName; BtnUpdateDmk.Visibility = Visibility.Visible; }
        else TxtDmkUpdateStatus.Text = "Up to date";
        BtnCheckDmk.IsEnabled = true;
    }

    private async void BtnUpdateDmk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingDmkUrl)) return;
        BtnUpdateDmk.IsEnabled = false; TxtDmkUpdateStatus.Text = "Downloading...";
        string tmp = Path.Combine(Path.GetTempPath(), "gw2dmk_update.tar.gz");
        SetInstalling(true);
        try
        {
            await UpdateChecker.DownloadAsync(_pendingDmkUrl, tmp); TxtDmkUpdateStatus.Text = "Installing...";
            await UpdateChecker.InstallDmkFromArchive(tmp, UpdateChecker.DmkInstallDirectory);
            try { File.Delete(tmp); } catch { }
            var s = SettingsManager.Load(); s.DmkToolPath = UpdateChecker.DmkInstallDirectory; s.DmkInstalledTag = _pendingDmkTag ?? "installed"; SaveChanges(s);
            TxtDmkInstalled.Text = string.Concat("installed: ", s.DmkInstalledTag); TxtDmkUpdateStatus.Text = "Installed."; BtnUpdateDmk.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { TxtDmkUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateDmk.IsEnabled = true; }
        finally { SetInstalling(false); }
    }

    private async void BtnUpdateHw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHwUrl)) return;
        BtnUpdateHw.IsEnabled = false;
        TxtHwUpdateStatus.Text = "Downloading...";
        SetInstalling(true);
        try
        {
            string newExe = Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.new.exe");
            await UpdateChecker.DownloadAsync(_pendingHwUrl, newExe);
            UpdateChecker.LaunchSelfUpdateScript(newExe, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.exe"));
            Application.Current.Shutdown();
        }
        catch (Exception ex) { TxtHwUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateHw.IsEnabled = true; }
        finally { SetInstalling(false); }
    }

    private async void BtnUpdateGw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingGwUrl)) return;
        BtnUpdateGw.IsEnabled = false;
        TxtGwUpdateStatus.Text = "Downloading...";
        var s = SettingsManager.Load();
        string gwDir = string.IsNullOrEmpty(s.GwPath)
            ? Path.Combine(AppContext.BaseDirectory, "greaseweazle")
            : Path.GetDirectoryName(s.GwPath) ?? AppContext.BaseDirectory;
        string tmp = Path.Combine(Path.GetTempPath(), "gw_update.zip");
        SetInstalling(true);
        try
        {
            await UpdateChecker.DownloadAsync(_pendingGwUrl, tmp);
            TxtGwUpdateStatus.Text = "Extracting...";
            await UpdateChecker.InstallGwFromZip(tmp, gwDir);
            try { File.Delete(tmp); } catch { }
            TxtGwUpdateStatus.Text = "Updated. Restart to apply.";
            BtnUpdateGw.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { TxtGwUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateGw.IsEnabled = true; }
        finally { SetInstalling(false); }
    }

    private async void BtnUpdateHxc_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHxcUrl)) return;
        BtnUpdateHxc.IsEnabled = false;
        TxtHxcUpdateStatus.Text = "Downloading...";
        string installDir = Path.Combine(AppContext.BaseDirectory, "hxc");
        string tmp = Path.Combine(Path.GetTempPath(), "hxc_update.zip");
        SetInstalling(true);
        try
        {
            await UpdateChecker.DownloadAsync(_pendingHxcUrl, tmp);
            TxtHxcUpdateStatus.Text = "Extracting...";
            await UpdateChecker.InstallHxcFromZip(tmp, installDir);
            try { File.Delete(tmp); } catch { }
            string hxcExe = Path.Combine(installDir, "HxCFloppyEmulator.exe");
            var s = SettingsManager.Load();
            s.HxcPath         = hxcExe;
            s.HxcInstalledTag = _pendingHxcTag ?? _pendingHxcUrl ?? "";
            SaveChanges(s);
            TxtHxcPath.Text      = hxcExe;
            TxtHxcInstalled.Text = string.Concat("installed: ", s.HxcInstalledTag);
            TxtHxcUpdateStatus.Text = "Updated.";
            BtnUpdateHxc.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { TxtHxcUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateHxc.IsEnabled = true; }
        finally { SetInstalling(false); }
    }

    private void BtnChangeGw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Locate gw.exe", Filter = ExeFilter };
        if (dlg.ShowDialog() == true)
        {
            var s = SettingsManager.Load();
            s.GwPath = dlg.FileName;
            SaveChanges(s);
            TxtGwPath.Text = dlg.FileName;
        }
    }

    private void BtnChangeHxc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Locate HxCFloppyEmulator.exe", Filter = ExeFilter };
        if (dlg.ShowDialog() == true)
        {
            var s = SettingsManager.Load();
            s.HxcPath = dlg.FileName;
            SaveChanges(s);
            TxtHxcPath.Text = dlg.FileName;
        }
    }
}
