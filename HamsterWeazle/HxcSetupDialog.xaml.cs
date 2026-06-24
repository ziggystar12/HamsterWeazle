using System.IO;
using System.Windows;
using System.Windows.Input;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class HxcSetupDialog : Window
{
    public string? HxcPath { get; private set; }
    public string? InstalledTag { get; private set; }
    public bool Skipped { get; private set; }

    private GhRelease? _release;
    private static readonly string Wc = ((char)42).ToString();

    public HxcSetupDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _release = await UpdateChecker.GetLatestReleaseAsync("jfdelnero", "HxCFloppyEmulator");
            if (_release != null && !string.IsNullOrEmpty(_release.DownloadUrl))
            {
                BtnDownload.Content  = string.Concat("Download HxCFloppyEmulator  (", _release.TagName, ")");
                BtnDownload.IsEnabled = true;
            }
            else
            {
                BtnDownload.Content   = "Could not reach GitHub";
                BtnDownload.IsEnabled = false;
            }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    { if (e.ClickCount != 2) DragMove(); }

    private async void BtnDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null || string.IsNullOrEmpty(_release.DownloadUrl)) return;
        BtnDownload.IsEnabled = false;
        BtnSkip.IsEnabled     = false;
        ProgressBar.Visibility = Visibility.Visible;
        TxtStatus.Visibility   = Visibility.Visible;
        TxtStatus.Text         = "Connecting...";

        string installDir = Path.Combine(AppContext.BaseDirectory, "hxc");
        string tmp        = Path.Combine(Path.GetTempPath(), "hxc_setup.zip");

        try
        {
            var prog = new Progress<int>(p => Dispatcher.InvokeAsync(() =>
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Maximum        = 100;
                ProgressBar.Value          = p;
                TxtStatus.Text             = string.Concat("Downloading... ", p, "%");
            }));

            await UpdateChecker.DownloadAsync(_release.DownloadUrl, tmp, prog);
            ProgressBar.IsIndeterminate = true;
            TxtStatus.Text = "Extracting...";
            await UpdateChecker.InstallHxcFromZip(tmp, installDir);
            try { File.Delete(tmp); } catch { }

            string guiExe = Path.Combine(installDir, "HxCFloppyEmulator.exe");
            if (!File.Exists(guiExe))
            { SetError("Could not find HxCFloppyEmulator.exe in download."); return; }

            HxcPath      = guiExe;
            InstalledTag = _release.TagName;
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { SetError(string.Concat("Download failed: ", ex.Message)); }
    }

    private void SetError(string msg)
    {
        TxtStatus.Text         = msg;
        ProgressBar.Visibility = Visibility.Collapsed;
        BtnDownload.IsEnabled  = true;
        BtnSkip.IsEnabled      = true;
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    { Skipped = true; DialogResult = false; Close(); }
}
