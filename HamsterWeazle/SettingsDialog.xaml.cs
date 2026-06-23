using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class SettingsDialog : Window
{
    private readonly string _gwPath;
    private readonly string _gwVersion;

    public SettingsDialog(string gwPath, string gwVersion)
    {
        _gwPath    = gwPath;
        _gwVersion = gwVersion;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        string appVer = UpdateChecker.CurrentAppVersion();
        TxtVersion.Text      = string.Concat("Version ", appVer);
        TxtHwInstalled.Text  = string.Concat("installed: v", appVer);
        TxtGwInstalled.Text  = string.IsNullOrEmpty(_gwVersion)
            ? "not found"
            : string.Concat("installed: ", _gwVersion);

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
        BtnCheckHw.IsEnabled    = false;
        TxtHwUpdateStatus.Text  = "Checking...";
        var rel = await UpdateChecker.GetLatestReleaseAsync("ziggystar12", "HamsterWeazle");
        if (rel == null)
        {
            TxtHwUpdateStatus.Text = "Could not reach GitHub";
        }
        else
        {
            string cur = UpdateChecker.CurrentAppVersion();
            if (UpdateChecker.IsNewer(rel.TagName, cur))
                TxtHwUpdateStatus.Text = string.Concat(rel.TagName, " available");
            else
                TxtHwUpdateStatus.Text = "Up to date";
        }
        BtnCheckHw.IsEnabled = true;
    }

    private async void BtnCheckGw_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckGw.IsEnabled   = false;
        TxtGwUpdateStatus.Text = "Checking...";
        var rel = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");
        if (rel == null)
        {
            TxtGwUpdateStatus.Text = "Could not reach GitHub";
        }
        else
        {
            string cur = _gwVersion.TrimStart('v');
            if (string.IsNullOrEmpty(cur))
                TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available - install gw.exe first");
            else if (UpdateChecker.IsNewer(rel.TagName, cur))
                TxtGwUpdateStatus.Text = string.Concat(rel.TagName, " available");
            else
                TxtGwUpdateStatus.Text = "Up to date";
        }
        BtnCheckGw.IsEnabled = true;
    }
}
