using System.IO;
using System.Windows;
using System.Windows.Input;
using HamsterWeazle.Services;
using Microsoft.Win32;

namespace HamsterWeazle;

public partial class SetupDialog : Window
{
    public string? GwExePath { get; private set; }

    private GhRelease? _latestRelease;
    private static readonly string Wc        = ((char)42).ToString();
    private static readonly string ExeFilter = string.Concat("gw.exe|gw.exe|Executables|", Wc, ".exe");

    public SetupDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => await CheckLatestAsync();
    }

    private async Task CheckLatestAsync()
    {
        _latestRelease = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");
        if (_latestRelease != null)
            BtnDownload.Content  = string.Concat("Download Latest  (", _latestRelease.TagName, ")");
        else
            BtnDownload.Content  = "Download Latest";
        BtnDownload.IsEnabled = _latestRelease != null && !string.IsNullOrEmpty(_latestRelease.DownloadUrl);
    }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_latestRelease == null || string.IsNullOrEmpty(_latestRelease.DownloadUrl)) return;

        BtnDownload.IsEnabled = false;
        BtnBrowse.IsEnabled   = false;
        BtnSkip.IsEnabled     = false;
        ProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Visibility   = Visibility.Visible;
        TxtStatus.Text         = "Connecting...";

        string installDir = Path.Combine(AppContext.BaseDirectory, "greaseweazle");
        string zipPath    = Path.Combine(Path.GetTempPath(), "gw_setup.zip");

        try
        {
            var prog = new Progress<int>(p =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    ProgressBar.Value  = p;
                    TxtStatus.Text     = string.Concat("Downloading... ", p, "%");
                });
            });

            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100;
            await UpdateChecker.DownloadAsync(_latestRelease.DownloadUrl, zipPath, prog);

            ProgressBar.IsIndeterminate = true;
            TxtStatus.Text = "Extracting...";
            await UpdateChecker.InstallGwFromZip(zipPath, installDir);

            string gwExe = Path.Combine(installDir, "gw.exe");
            if (!File.Exists(gwExe))
            {
                SetError("Could not find gw.exe in the download. Try Browse instead.");
                return;
            }

            GwExePath  = gwExe;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SetError(string.Concat("Download failed: ", ex.Message));
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }

    private void SetError(string msg)
    {
        TxtStatus.Text        = msg;
        ProgressBar.Visibility = Visibility.Collapsed;
        BtnBrowse.IsEnabled   = true;
        BtnSkip.IsEnabled     = true;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Locate gw.exe", Filter = ExeFilter };
        if (dlg.ShowDialog() == true)
        {
            GwExePath    = dlg.FileName;
            DialogResult = true;
            Close();
        }
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    { DialogResult = false; Close(); }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount != 2) DragMove(); }
}
