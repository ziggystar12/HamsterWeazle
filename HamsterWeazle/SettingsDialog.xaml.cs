using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using HamsterWeazle.Services;
using Microsoft.Win32;

namespace HamsterWeazle;

public partial class SettingsDialog : Window
{
    private string? _pendingHwUrl;
    private string? _pendingGwUrl;
    private string? _pendingHxcUrl;
    private string? _pendingHxcTag;
    private static readonly string Wc = ((char)42).ToString();
    private static readonly string ExeFilter = string.Concat("Executables|", Wc, ".exe");

    public SettingsDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    private async Task OnLoadedAsync()
    {
        string appVer = UpdateChecker.CurrentAppVersion();
        TxtVersion.Text     = string.Concat("Version ", appVer);
        TxtHwInstalled.Text = string.Concat("installed: v", appVer);

        var s = SettingsManager.Load();

        TxtGwPath.Text      = s.GwPath ?? "not configured";
        TxtHxcPath.Text     = s.HxcPath ?? "not installed";

        if (!string.IsNullOrEmpty(s.GwPath) && File.Exists(s.GwPath))
        {
            string ver = await GwRunner.GetVersionAsync(s.GwPath);
            TxtGwInstalled.Text = string.IsNullOrEmpty(ver) ? "found" : string.Concat("installed: ", ver);
        }
        else
        {
            TxtGwInstalled.Text = "not configured";
        }

        TxtHxcInstalled.Text = string.IsNullOrEmpty(s.HxcInstalledTag)
            ? "not installed"
            : string.Concat("installed: ", s.HxcInstalledTag);

        string theme = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
        RbDark.IsChecked  = theme == "Dark";
        RbAmiga.IsChecked = theme == "Amiga";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount != 2) DragMove(); }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void RbTheme_Checked(object sender, RoutedEventArgs e)
    {
        string next = RbAmiga.IsChecked == true ? "Amiga" : "Dark";
        string cur  = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
        if (next == cur) return;
        App.SwitchTheme(next);
        var s = SettingsManager.Load();
        s.Theme = next;
        SettingsManager.Save(s);
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

    private async void BtnUpdateHw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHwUrl)) return;
        BtnUpdateHw.IsEnabled = false;
        TxtHwUpdateStatus.Text = "Downloading...";
        try
        {
            string newExe = Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.new.exe");
            await UpdateChecker.DownloadAsync(_pendingHwUrl, newExe);
            UpdateChecker.LaunchSelfUpdateScript(newExe, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.exe"));
            Application.Current.Shutdown();
        }
        catch (Exception ex) { TxtHwUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateHw.IsEnabled = true; }
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
    }

    private async void BtnUpdateHxc_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_pendingHxcUrl)) return;
        BtnUpdateHxc.IsEnabled = false;
        TxtHxcUpdateStatus.Text = "Downloading...";
        string installDir = Path.Combine(AppContext.BaseDirectory, "hxc");
        string tmp = Path.Combine(Path.GetTempPath(), "hxc_update.zip");
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
            SettingsManager.Save(s);
            TxtHxcPath.Text      = hxcExe;
            TxtHxcInstalled.Text = string.Concat("installed: ", s.HxcInstalledTag);
            TxtHxcUpdateStatus.Text = "Updated.";
            BtnUpdateHxc.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { TxtHxcUpdateStatus.Text = string.Concat("Failed: ", ex.Message); BtnUpdateHxc.IsEnabled = true; }
    }

    private void BtnChangeGw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Locate gw.exe", Filter = ExeFilter };
        if (dlg.ShowDialog() == true)
        {
            var s = SettingsManager.Load();
            s.GwPath = dlg.FileName;
            SettingsManager.Save(s);
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
            SettingsManager.Save(s);
            TxtHxcPath.Text = dlg.FileName;
        }
    }
}
