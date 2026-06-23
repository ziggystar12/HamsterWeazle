using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class SettingsDialog : Window
{
    private readonly string _gwVersion;
    private readonly string _hxcTag;

    public SettingsDialog(string gwVersion, string hxcTag)
    {
        _gwVersion = gwVersion;
        _hxcTag    = hxcTag;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        string appVer = UpdateChecker.CurrentAppVersion();
        TxtVersion.Text     = string.Concat("Version ", appVer);
        TxtHwInstalled.Text = string.Concat("installed: v", appVer);

        TxtGwInstalled.Text = string.IsNullOrEmpty(_gwVersion)
            ? "not installed"
            : string.Concat("installed: ", _gwVersion);

        TxtHxcInstalled.Text = string.IsNullOrEmpty(_hxcTag)
            ? "not installed"
            : string.Concat("installed: ", _hxcTag);

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
    { OpenUrl("https://github.com/ziggystar12/HamsterWeazle"); }

    private void BtnGwGitHub_Click(object sender, RoutedEventArgs e)
    { OpenUrl("https://github.com/keirf/greaseweazle"); }

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
        var rel = await UpdateChecker.GetLatestReleaseAsync("ziggystar12", "HamsterWeazle");
        if (rel == null)
            TxtHwUpdateStatus.Text = "Could not reach GitHub";
        else
        {
            string cur = UpdateChecker.CurrentAppVersion();
            TxtHwUpdateStatus.Text = UpdateChecker.IsNewer(rel.TagName, cur)
                ? string.Concat(rel.TagName, " available")
                : "Up to date";
        }
        BtnCheckHw.IsEnabled = true;
    }

    private async void BtnCheckGw_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckGw.IsEnabled   = false;
        TxtGwUpdateStatus.Text = "Checking...";
        var rel = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");
        if (rel == null)
            TxtGwUpdateStatus.Text = "Could not reach GitHub";
        else if (string.IsNullOrEmpty(_gwVersion))
            TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available");
        else
            TxtGwUpdateStatus.Text = UpdateChecker.IsNewer(rel.TagName, _gwVersion)
                ? string.Concat(rel.TagName, " available")
                : "Up to date";
        BtnCheckGw.IsEnabled = true;
    }

    private async void BtnCheckHxc_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckHxc.IsEnabled   = false;
        TxtHxcUpdateStatus.Text = "Checking...";
        var rel = await UpdateChecker.GetLatestReleaseAsync("jfdelnero", "HxCFloppyEmulator");
        if (rel == null)
            TxtHxcUpdateStatus.Text = "Could not reach GitHub";
        else if (string.IsNullOrEmpty(_hxcTag))
            TxtHxcUpdateStatus.Text = string.Concat(rel.TagName, " available - not installed");
        else
            TxtHxcUpdateStatus.Text = UpdateChecker.IsNewer(rel.TagName, _hxcTag)
                ? string.Concat(rel.TagName, " available")
                : "Up to date";
        BtnCheckHxc.IsEnabled = true;
    }
}
