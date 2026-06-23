using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using HamsterWeazle.Models;
using HamsterWeazle.Services;
using Microsoft.Win32;

namespace HamsterWeazle;

public partial class MainWindow : Window
{
    private readonly GwRunner _runner = new();
    private AppSettings _settings;
    private IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> _allFormats = [];
    private GwOperation _currentOp = GwOperation.Read;

    public MainWindow()
    {
        _settings = SettingsManager.Load();
        App.SwitchTheme(_settings.Theme);
        InitializeComponent();
        Width  = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _runner.OutputReceived += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _runner.ProcessExited  += code => Dispatcher.InvokeAsync(() => { AppendLog(string.Concat(Environment.NewLine, "[exit code ", code, "]")); SetRunning(false); });
        Loaded  += async (_, _) => { LoadFormats(); await DetectGwAsync(); UpdateThemeButton(); UpdateTabUI(); };
        Closing += (_, _) => SaveSettings();
    }

    private void LoadFormats()
    {
        try { _allFormats = CfgParser.Parse(); }
        catch (Exception ex) { AppendLog(string.Concat("[warn] diskdefs parse failed: ", ex.Message)); }
        CboVendor.Items.Clear();
        foreach (var (v, _) in _allFormats) CboVendor.Items.Add(v);
        int idx = CboVendor.Items.IndexOf(_settings.LastVendor);
        CboVendor.SelectedIndex = idx >= 0 ? idx : (CboVendor.Items.Count > 0 ? 0 : -1);
    }

    private async Task DetectGwAsync()
    {
        string? path = _settings.GwPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = GwRunner.FindGwExe();
        if (path != null)
        {
            _runner.GwPath   = path;
            _settings.GwPath = path;
            string ver = await GwRunner.GetVersionAsync(path);
            TxtGwStatus.Text    = string.Concat("gw.exe  ", ver, "  |  ", path);
            BtnLocateGw.Content = "Change...";
        }
        else
        {
            TxtGwStatus.Text    = "gw.exe: not found - click Locate";
            BtnLocateGw.Content = "Locate...";
        }
    }

    private void UpdateThemeButton() =>
        BtnTheme.Content = Application.Current.Resources["Win.ThemeToggleLabel"] as string ?? "Amiga";

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        string cur  = Application.Current.Resources["Win.ThemeName"] as string ?? "Dark";
        string next = cur == "Dark" ? "Amiga" : "Dark";
        App.SwitchTheme(next);
        _settings.Theme = next;
        UpdateThemeButton();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void BtnMinimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object s, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object s, EventArgs e) =>
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "#" : "[]";

    private void OpTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        _currentOp = tb.Tag?.ToString() switch
        {
            "Write" => GwOperation.Write,
            "Erase" => GwOperation.Erase,
            "Info"  => GwOperation.Info,
            _       => GwOperation.Read,
        };
        foreach (var t in new[] { TabRead, TabWrite, TabErase, TabInfo })
            t.IsChecked = t == tb;
        UpdateTabUI();
        UpdateCommandPreview();
    }

    private void UpdateTabUI()
    {
        bool rw = _currentOp is GwOperation.Read or GwOperation.Write;
        PanelFormat.Visibility = rw ? Visibility.Visible : Visibility.Collapsed;
        PanelFile.Visibility   = rw ? Visibility.Visible : Visibility.Collapsed;
        PanelErase.Visibility  = _currentOp == GwOperation.Erase ? Visibility.Visible : Visibility.Collapsed;
        AdvExpander.Visibility = rw ? Visibility.Visible : Visibility.Collapsed;
        LblFile.Content        = _currentOp == GwOperation.Read ? "Output" : "Input";
    }

    private void CboVendor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CboFormat.Items.Clear();
        if (CboVendor.SelectedItem is string vendor)
        {
            var entry = _allFormats.FirstOrDefault(x => x.Vendor == vendor);
            foreach (var fmt in entry.Formats ?? []) CboFormat.Items.Add(fmt);
            var last = entry.Formats?.FirstOrDefault(f => f.FullName == _settings.LastFormat);
            CboFormat.SelectedItem = last ?? (CboFormat.Items.Count > 0 ? CboFormat.Items[0] : null);
        }
        UpdateCommandPreview();
    }

    private void CboFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboFormat.SelectedItem is DiskFormat fmt) _settings.LastFormat = fmt.FullName;
        UpdateCommandPreview();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        string dir    = string.IsNullOrEmpty(_settings.LastOutputDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _settings.LastOutputDir;
        string filter = "Disk images|*.img;*.hfe;*.scp|All files|*.*";

        if (_currentOp == GwOperation.Read)
        {
            var dlg = new SaveFileDialog { Title = "Save disk image as...", Filter = filter, InitialDirectory = dir };
            if (dlg.ShowDialog() == true)
            { TxtFile.Text = dlg.FileName; _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? ""; }
        }
        else
        {
            var dlg = new OpenFileDialog { Title = "Open disk image...", Filter = filter, InitialDirectory = dir };
            if (dlg.ShowDialog() == true)
            { TxtFile.Text = dlg.FileName; _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? ""; }
        }
        UpdateCommandPreview();
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => UpdateCommandPreview();
    private void Options_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateCommandPreview();

    private void UpdateCommandPreview()
    {
        if (TxtCmdPreview == null) return;
        string format   = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string filePath = TxtFile?.Text.Trim() ?? "";
        if (_currentOp is GwOperation.Read or GwOperation.Write
            && (string.IsNullOrEmpty(format) || string.IsNullOrEmpty(filePath)))
        {
            TxtCmdPreview.Text = "(select format and file to preview the command)";
            return;
        }
        string args   = _runner.BuildArguments(_currentOp, format, filePath, BuildCurrentOptions());
        string gwName = string.IsNullOrEmpty(_runner.GwPath) ? "gw.exe" : Path.GetFileName(_runner.GwPath);
        TxtCmdPreview.Text = string.Concat(gwName, " ", args);
    }

    private GwOptions BuildCurrentOptions()
    {
        int.TryParse(TxtCylStart?.Text, out int s);
        int.TryParse(TxtCylEnd?.Text,   out int e2);
        int.TryParse(TxtRetries?.Text,  out int r);
        if (r == 0) r = 3;
        bool range = s != 0 || e2 != 79;
        return new GwOptions(StartCyl: range ? s : null, EndCyl: range ? e2 : null,
                             Retries: r, Verify: ChkVerify?.IsChecked == true);
    }

    private async void BtnLocateGw_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Locate gw.exe", Filter = "gw.exe|gw.exe|Executables|*.exe" };
        if (dlg.ShowDialog() == true)
        {
            _runner.GwPath   = dlg.FileName;
            _settings.GwPath = dlg.FileName;
            string ver = await GwRunner.GetVersionAsync(dlg.FileName);
            TxtGwStatus.Text    = string.Concat("gw.exe  ", ver, "  |  ", dlg.FileName);
            BtnLocateGw.Content = "Change...";
            UpdateCommandPreview();
        }
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured. Use Locate in the status bar."); return; }
        if (_currentOp is GwOperation.Read or GwOperation.Write)
        {
            if (CboFormat.SelectedItem is not DiskFormat)
            { AppendLog("[error] Please select a disk format."); return; }
            if (string.IsNullOrWhiteSpace(TxtFile.Text))
            { AppendLog("[error] Please specify an image file path."); return; }
        }
        string format   = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string filePath = TxtFile.Text.Trim();
        string args     = _runner.BuildArguments(_currentOp, format, filePath, BuildCurrentOptions());
        SetRunning(true);
        AppendLog(string.Concat("$ gw.exe ", args));
        AppendLog("");
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    { _runner.Cancel(); AppendLog("[cancelling...]"); }

    private void SetRunning(bool running) => Dispatcher.InvokeAsync(() =>
    {
        BtnRun.IsEnabled       = !running;
        BtnCancel.IsEnabled    = running;
        ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    });

    private void AppendLog(string line)
    { TxtLog.AppendText(line + "\r\n"); LogScroll.ScrollToBottom(); }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private void SaveSettings()
    {
        _settings.WindowWidth  = ActualWidth;
        _settings.WindowHeight = ActualHeight;
        if (CboVendor.SelectedItem is string v) _settings.LastVendor = v;
        SettingsManager.Save(_settings);
    }
}
