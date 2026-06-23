using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HamsterWeazle.Models;
using HamsterWeazle.Services;
using Microsoft.Win32;

namespace HamsterWeazle;

public partial class MainWindow : Window
{
    private readonly GwRunner _runner = new();
    private readonly GwRunner _hxcRunner = new();
    private AppSettings _settings;
    private IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> _allFormats = [];
    private GwOperation _currentOp = GwOperation.Read;

    private static readonly string Wc = ((char)42).ToString();
    private static readonly string ImgFilter  = string.Concat("Disk images|",Wc,".img;",Wc,".hfe;",Wc,".scp;",Wc,".adf|All files|",Wc);
    private static readonly string ExeFilter  = string.Concat("gw.exe|gw.exe|Executables|",Wc,".exe");

    public MainWindow()
    {
        _settings = SettingsManager.Load();
        App.SwitchTheme(_settings.Theme);
        InitializeComponent();
        Width  = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _runner.OutputReceived += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _runner.ProcessExited  += code => Dispatcher.InvokeAsync(() => OnProcessDone(code));
        Loaded  += async (_, _) =>
        {
            TxtTitleVersion.Text = string.Concat("v", UpdateChecker.CurrentAppVersion());
            LoadFormats(); RestoreLastOp(); await DetectGwAsync(); await DetectHxcAsync();
            UpdateTabUI(); RestoreLastFilePath(); UpdateCommandPreview(); RefreshSidebar();
        };
        Closing += (_, _) => SaveSettings();
    }

    private void LoadFormats()
    {
        try { _allFormats = CfgParser.Parse(); }
        catch (Exception ex) { AppendLog(string.Concat("[warn] diskdefs: ", ex.Message)); }
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
            _ = CheckForUpdatesAsync(ver);
        }
        else
        {
            var dlg = new SetupDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.GwExePath))
            {
                _runner.GwPath   = dlg.GwExePath;
                _settings.GwPath = dlg.GwExePath;
                SettingsManager.Save(_settings);
                string ver = await GwRunner.GetVersionAsync(dlg.GwExePath);
                _ = CheckForUpdatesAsync(ver);
            }
            else
            {
                _ = CheckForUpdatesAsync("");
            }
        }
    }

    private Task DetectHxcAsync()
    {
        string? path = _settings.HxcPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = UpdateChecker.FindHxcGuiExe();
        if (path != null)
        {
            _settings.HxcPath = path;
            string? cli = UpdateChecker.FindHxcCliExe();
            if (cli != null) _hxcRunner.GwPath = cli;
        }
        return Task.CompletedTask;
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
            "Tools" => GwOperation.Tools,
            "Info"  => GwOperation.Info,
            _       => GwOperation.Read,
        };
        foreach (var t in new[] { TabRead, TabWrite, TabErase, TabTools, TabInfo })
            t.IsChecked = t == tb;
        UpdateTabUI();
        UpdateCommandPreview();
    }

    private void UpdateTabUI()
    {
        bool rw    = _currentOp is GwOperation.Read or GwOperation.Write;
        bool tools = _currentOp == GwOperation.Tools;
        bool erase = _currentOp == GwOperation.Erase;
        PanelFormat.Visibility = rw    ? Visibility.Visible : Visibility.Collapsed;
        PanelFile.Visibility   = rw    ? Visibility.Visible : Visibility.Collapsed;
        PanelErase.Visibility  = erase ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility  = tools ? Visibility.Visible : Visibility.Collapsed;
        AdvExpander.Visibility = rw    ? Visibility.Visible : Visibility.Collapsed;
        if (PanelRevs != null)
            PanelRevs.Visibility = _currentOp == GwOperation.Read ? Visibility.Visible : Visibility.Collapsed;
        BtnRun.Visibility      = tools ? Visibility.Collapsed : Visibility.Visible;
        BtnCancel.Visibility   = tools ? Visibility.Collapsed : Visibility.Visible;
        BtnRun.Content = _currentOp switch
        {
            GwOperation.Read  => "  READ  ",
            GwOperation.Write => "  WRITE  ",
            GwOperation.Erase => "  ERASE  ",
            _                 => "  RUN  ",
        };
        if (BtnAutoRead != null)
            BtnAutoRead.Visibility = _currentOp == GwOperation.Read ? Visibility.Visible : Visibility.Collapsed;
        LblFile.Content = _currentOp == GwOperation.Read ? "Save to:" : "Image file:";
        if (TxtAutoDetect != null) TxtAutoDetect.Visibility = Visibility.Collapsed;
        bool customOut = ChkCustomOutput?.IsChecked == true;
        if (PanelFile != null)
            PanelFile.Visibility = _currentOp == GwOperation.Write || (_currentOp == GwOperation.Read && customOut)
                ? Visibility.Visible : Visibility.Collapsed;
        if (_currentOp == GwOperation.Read && !customOut && TxtFile != null)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            TxtFile.Text = Path.Combine(GetInboxDir(), string.Concat("disk_", ts, ".img"));
        }
    }

    private void ChkCustomOutput_Changed(object sender, RoutedEventArgs e) => UpdateTabUI();

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
        string dir = string.IsNullOrEmpty(_settings.LastOutputDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _settings.LastOutputDir;
        if (_currentOp == GwOperation.Read)
        {
            var dlg = new SaveFileDialog { Title = "Save disk image as...", Filter = ImgFilter, InitialDirectory = GetInboxDir() };
            if (dlg.ShowDialog() == true)
            { TxtFile.Text = dlg.FileName; _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? ""; }
        }
        else
        {
            var dlg = new OpenFileDialog { Title = "Open disk image...", Filter = ImgFilter, InitialDirectory = dir };
            if (dlg.ShowDialog() == true)
            { TxtFile.Text = dlg.FileName; _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? ""; }
        }
        UpdateCommandPreview();
    }

    private void Options_Changed(object sender, RoutedEventArgs e) => UpdateCommandPreview();
    private void Options_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (sender == TxtFile && _currentOp == GwOperation.Write)
            TryAutoDetectFormat(TxtFile.Text.Trim());
        UpdateCommandPreview();
    }

    private void TryAutoDetectFormat(string path)
    {
        string? fullName = FormatGuesser.Guess(path);
        if (fullName == null) return;
        string[] parts = fullName.Split('.');
        if (parts.Length < 2) return;
        string vendor = string.Concat(char.ToUpper(parts[0][0]), parts[0][1..]);
        string vendorKey = parts[0];
        var entry = _allFormats.FirstOrDefault(x =>
            x.Vendor.StartsWith(char.ToUpper(vendorKey[0]).ToString(), StringComparison.OrdinalIgnoreCase) ||
            x.Formats.Any(f => f.FullName.StartsWith(vendorKey + ".", StringComparison.OrdinalIgnoreCase)));
        if (entry == default) return;
        var fmt = entry.Formats.FirstOrDefault(f => f.FullName == fullName);
        if (fmt == null) return;
        if (CboVendor.SelectedItem?.ToString() != entry.Vendor)
        {
            CboVendor.SelectedItem = entry.Vendor;
        }
        if (CboFormat.SelectedItem is not DiskFormat cur || cur.FullName != fullName)
        {
            CboFormat.SelectedItem = fmt;
            TxtAutoDetect.Visibility = Visibility.Visible;
            TxtAutoDetect.Text = string.Concat("Auto-detected: ", fullName);
        }
    }

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
        string? drive = RbDriveA?.IsChecked == true ? "0" : RbDriveB?.IsChecked == true ? "1" : null;
        int.TryParse(TxtRevs?.Text, out int revs);
        return new GwOptions(StartCyl: range ? s : null, EndCyl: range ? e2 : null,
                             Retries: r, Verify: ChkVerify?.IsChecked == true,
                             Drive: drive, Revs: revs > 1 ? revs : null);
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured. Open Settings to locate it."); return; }
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
        if (_currentOp == GwOperation.Write)
            PushToWriteQueue(filePath, format);
        SetRunning(true);
        AppendLog(string.Concat("$ gw.exe ", args));
        AppendLog("");
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private async void QuickWrite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WriteQueueItem item) return;
        if (!item.FileExists)
        { AppendLog(string.Concat("[error] File not found: ", item.FilePath)); return; }
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }
        string args = _runner.BuildArguments(GwOperation.Write, item.Format, item.FilePath, new GwOptions());
        PushToWriteQueue(item.FilePath, item.Format);
        SetRunning(true);
        AppendLog(string.Concat("[quick write] $ gw.exe ", args));
        AppendLog("");
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _autoCts?.Cancel();
        _runner.Cancel();
        AppendLog("[cancelling...]");
    }

    private void SetRunning(bool running) => Dispatcher.InvokeAsync(() =>
    {
        BtnRun.IsEnabled       = !running;
        BtnCancel.IsEnabled    = running;
        ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    });

    private void OnProcessDone(int code)
    {
        AppendLog(string.Concat(Environment.NewLine, "[exit code ", code, "]"));
        SetRunning(false);
        if (_currentOp == GwOperation.Read) RefreshInbox();
    }

    private void AppendLog(string line)
    { TxtLog.AppendText(line + "\r\n"); LogScroll.ScrollToBottom(); }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

    private string GetInboxDir()
    {
        string dir = string.IsNullOrEmpty(_settings.InboxDir)
            ? Path.Combine(AppContext.BaseDirectory, "inbox")
            : _settings.InboxDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void PushToWriteQueue(string filePath, string format)
    {
        var items = _settings.WriteQueueItems;
        items.RemoveAll(i => i.FilePath == filePath && i.Format == format);
        items.Insert(0, new WriteQueueItem { FilePath = filePath, Format = format, LastWritten = DateTime.Now });
        if (items.Count > 30) items.RemoveRange(30, items.Count - 30);
        RefreshWriteQueue();
    }

    private void RefreshSidebar() { RefreshWriteQueue(); RefreshInbox(); }

    private void RefreshWriteQueue()
    {
        if (WriteQueuePanel == null) return;
        WriteQueuePanel.Children.Clear();
        if (_settings.WriteQueueItems.Count == 0)
        {
            var empty = new TextBlock { Text = "No writes yet.", Margin = new Thickness(10, 8, 10, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
            empty.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
            WriteQueuePanel.Children.Add(empty);
            return;
        }
        foreach (var item in _settings.WriteQueueItems)
            WriteQueuePanel.Children.Add(BuildQueueCard(item));
    }

    private FrameworkElement BuildQueueCard(WriteQueueItem item)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Win.Panel2");

        var sp = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fmtTxt = new TextBlock { Text = item.Format, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        fmtTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.Accent");
        fmtTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
        Grid.SetColumn(fmtTxt, 0);

        var runBtn = new Button { Content = "Run", Tag = item, Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
        runBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        runBtn.IsEnabled = item.FileExists;
        runBtn.Click += QuickWrite_Click;
        Grid.SetColumn(runBtn, 1);

        row1.Children.Add(fmtTxt);
        row1.Children.Add(runBtn);

        var nameTxt = new TextBlock { Text = item.FileName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        nameTxt.SetResourceReference(TextBlock.ForegroundProperty, item.FileExists ? "Win.Text" : "Win.Error");
        nameTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMono");

        var pathTxt = new TextBlock { Text = item.ShortPath, TextTrimming = TextTrimming.CharacterEllipsis };
        pathTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        pathTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");

        var dateTxt = new TextBlock { Text = item.DateLabel };
        dateTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        dateTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");

        sp.Children.Add(row1);
        sp.Children.Add(nameTxt);
        sp.Children.Add(pathTxt);
        sp.Children.Add(dateTxt);

        var hxcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

        var listBtn = new Button { Content = "List Files", Tag = item.FilePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand, IsEnabled = item.FileExists };
        listBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "Open HxC", Tag = item.FilePath,
            Padding = new Thickness(6, 2, 6, 2), Height = 22, FontSize = 10,
            Cursor = Cursors.Hand,
            IsEnabled = item.FileExists && !string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath) };
        openBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        openBtn.Click += HxcOpen_Click;

        hxcRow.Children.Add(listBtn);
        hxcRow.Children.Add(openBtn);
        sp.Children.Add(hxcRow);

        border.Child = sp;
        return border;
    }

    private void RefreshInbox()
    {
        if (InboxPanel == null) return;
        InboxPanel.Children.Clear();
        string dir = GetInboxDir();
        var files = Directory.GetFiles(dir)
            .OrderByDescending(File.GetLastWriteTime)
            .Take(50)
            .ToList();
        if (files.Count == 0)
        {
            var empty = new TextBlock { Text = "No images yet.", Margin = new Thickness(10, 8, 10, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
            empty.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
            InboxPanel.Children.Add(empty);
            return;
        }
        foreach (string f in files)
            InboxPanel.Children.Add(BuildInboxCard(f));
    }

    private FrameworkElement BuildInboxCard(string filePath)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand };
        border.SetResourceReference(Border.BackgroundProperty, "Win.Panel2");
        border.MouseLeftButtonUp += (_, _) =>
        {
            TxtFile.Text = filePath;
            TabWrite.IsChecked = true;
            OpTab_Click(TabWrite, new RoutedEventArgs());
        };

        var fi   = new FileInfo(filePath);
        long kb  = fi.Length >> 10;
        long mb  = fi.Length >> 20;
        string size = mb > 0
            ? string.Concat(mb.ToString("N1"), " MB")
            : string.Concat(kb.ToString("N0"), " KB");

        var sp  = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameTxt = new TextBlock { Text = fi.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.Text");
        nameTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMono");
        Grid.SetColumn(nameTxt, 0);

        var sizeTxt = new TextBlock { Text = size, Margin = new Thickness(4, 0, 0, 0) };
        sizeTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        sizeTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
        Grid.SetColumn(sizeTxt, 1);

        row.Children.Add(nameTxt);
        row.Children.Add(sizeTxt);
        sp.Children.Add(row);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

        var listBtn = new Button { Content = "List Files", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand };
        listBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "Open HxC", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Height = 22, FontSize = 10,
            Cursor = Cursors.Hand,
            IsEnabled = !string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath) };
        openBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        openBtn.Click += HxcOpen_Click;

        btnRow.Children.Add(listBtn);
        btnRow.Children.Add(openBtn);
        sp.Children.Add(btnRow);

        border.Child = sp;
        return border;
    }

    private CancellationTokenSource? _autoCts;

    private async void BtnAutoRead_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }

        string filePath = TxtFile?.Text.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var dlg = new SaveFileDialog { Title = "Save disk image as...", Filter = ImgFilter, InitialDirectory = GetInboxDir() };
            if (dlg.ShowDialog() != true) return;
            filePath = dlg.FileName;
            if (TxtFile != null) TxtFile.Text = filePath;
            _settings.LastOutputDir = Path.GetDirectoryName(filePath) ?? "";
        }

        var candidates = new[]
        {
            ("ibm.1440",       "IBM PC 1.44 MB HD"),
            ("ibm.720",        "IBM PC 720 KB DD"),
            ("amiga.amigados", "Amiga 880 KB DD"),
            ("atarist.720",    "Atari ST 720 KB"),
            ("ibm.1200",       "IBM PC 1.2 MB HD 5.25\""),
            ("ibm.360",        "IBM PC 360 KB DD 5.25\""),
        };

        SetRunning(true);
        _autoCts = new CancellationTokenSource();
        AppendLog("[auto read] Probing format — testing first 2 tracks with each candidate...");
        AppendLog("");

        string? bestFmt  = null;
        string? bestName = null;
        int     bestScore = -1;
        string  tmpProbe  = Path.Combine(Path.GetTempPath(), "hw_probe.img");

        foreach (var (fmt, name) in candidates)
        {
            string probeArgs = string.Concat("read --format ", fmt, " --tracks c=0-1 --retries 0 \"", tmpProbe, "\"");
            var sb = new System.Text.StringBuilder();

            var psi = new System.Diagnostics.ProcessStartInfo(_runner.GwPath, probeArgs)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

            using var p = System.Diagnostics.Process.Start(psi)!;
            string raw = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(_autoCts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } break; }

            int ok  = 0; int bad = 0;
            foreach (var line in raw.Split('\n'))
            {
                if (line.Contains("sectors OK")) ok++;
                if (line.Contains("No sector") || line.Contains("CRC error") || line.Contains("0/")) bad++;
            }
            int score = ok - bad;
            AppendLog(string.Concat("  ", name.PadRight(26), " score: ", score >= 0 ? "+" : "", score, "  (", ok, " OK, ", bad, " errors)"));

            if (score > bestScore) { bestScore = score; bestFmt = fmt; bestName = name; }
        }

        try { File.Delete(tmpProbe); } catch { }

        if (bestFmt == null || bestScore < 0)
        {
            AppendLog(string.Concat(Environment.NewLine, "[auto read] Could not identify format. Try selecting manually and using READ."));
            SetRunning(false);
            return;
        }

        AppendLog(string.Concat(Environment.NewLine, "[auto read] Best match: ", bestName, " (", bestFmt, ") — starting full read..."));
        AppendLog("");

        var entry = _allFormats.FirstOrDefault(x => x.Formats.Any(f => f.FullName == bestFmt));
        if (entry != default)
        {
            CboVendor.SelectedItem = entry.Vendor;
            var fmt2 = entry.Formats.FirstOrDefault(f => f.FullName == bestFmt);
            if (fmt2 != null) CboFormat.SelectedItem = fmt2;
        }

        string fullArgs = _runner.BuildArguments(GwOperation.Read, bestFmt, filePath, BuildCurrentOptions());
        PushToWriteQueue(filePath, bestFmt);
        try   { await _runner.RunAsync(fullArgs); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void BtnOpenInbox_Click(object sender, RoutedEventArgs e)
    { Process.Start("explorer.exe", GetInboxDir()); }

    private async void HxcList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        string? cli = _hxcRunner.GwPath ?? UpdateChecker.FindHxcCliExe();
        if (string.IsNullOrEmpty(cli))
        { AppendLog("[error] hxcfe.exe not found. Install HxCFloppyEmulator from Settings."); return; }
        _hxcRunner.GwPath = cli;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string listPath = filePath;
        string? tmpHfe = null;

        if (ext == ".img" || ext == ".ima" || ext == ".dsk" || ext == ".st")
        {
            tmpHfe = Path.Combine(Path.GetTempPath(), "hw_list_tmp.hfe");
            AppendLog(string.Concat("$ hxcfe.exe -finput:\"", filePath, "\" -conv:HXC_HFE -foutput:\"", tmpHfe, "\""));
            var convPsi = new System.Diagnostics.ProcessStartInfo(cli,
                string.Concat("-finput:\"", filePath, "\" -conv:HXC_HFE -foutput:\"", tmpHfe, "\""))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var convP = System.Diagnostics.Process.Start(convPsi)!;
            string convOut = await convP.StandardOutput.ReadToEndAsync() + await convP.StandardError.ReadToEndAsync();
            await convP.WaitForExitAsync();
            if (!File.Exists(tmpHfe) || new FileInfo(tmpHfe).Length == 0)
            {
                AppendLog("[error] Could not convert image. Try 'Open HxC' for manual browsing.");
                foreach (var l in convOut.Split('\n')) if (!string.IsNullOrWhiteSpace(l)) AppendLog(l.Trim());
                return;
            }
            AppendLog("");
            listPath = tmpHfe;
        }

        AppendLog(string.Concat("$ hxcfe.exe -list -finput:\"", listPath, "\""));
        AppendLog("");
        bool noLoader = false;
        void watch(string line) { if (line.Contains("No loader support")) noLoader = true; }
        _hxcRunner.OutputReceived += watch;
        try   { await _hxcRunner.RunAsync(string.Concat("-list -finput:\"", listPath, "\"")); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            _hxcRunner.OutputReceived -= watch;
            if (tmpHfe != null) try { File.Delete(tmpHfe); } catch { }
        }
        if (noLoader)
            AppendLog(string.Concat(Environment.NewLine, "[hint] hxcfe could not read this format. Try 'Open HxC' for manual browsing."));
    }

    private void HxcOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        string? gui = _settings.HxcPath ?? UpdateChecker.FindHxcGuiExe();
        if (string.IsNullOrEmpty(gui))
        { AppendLog("[error] HxCFloppyEmulator.exe not found."); return; }
        try { Process.Start(new ProcessStartInfo(gui, string.Concat("\"", filePath, "\"")) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
    }

    private async void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog { Owner = this };
        dlg.ShowDialog();
        _settings = SettingsManager.Load();
        await DetectHxcAsync();
        RefreshSidebar();
    }

    private async void BtnUpdateFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }
        SetRunning(true);
        AppendLog("$ gw.exe update");
        AppendLog("");
        try   { await _runner.RunAsync("update"); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private async void BtnCleanDrive_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }
        string driveArg = RbDriveB?.IsChecked == true ? " --drive 1" : "";
        SetRunning(true);
        AppendLog(string.Concat("$ gw.exe clean", driveArg));
        AppendLog("Insert cleaning disk now and ensure drive motor is running.");
        AppendLog("");
        try   { await _runner.RunAsync(string.Concat("clean", driveArg)); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void RestoreLastOp()
    {
        _currentOp = _settings.LastOp switch
        {
            "Write" => GwOperation.Write,
            "Erase" => GwOperation.Erase,
            "Tools" => GwOperation.Tools,
            "Info"  => GwOperation.Info,
            _       => GwOperation.Read,
        };
        foreach (var t in new[] { TabRead, TabWrite, TabErase, TabTools, TabInfo })
            t.IsChecked = t.Tag?.ToString() == _settings.LastOp;
    }

    private void RestoreLastFilePath()
    {
        if (!string.IsNullOrEmpty(_settings.LastFilePath))
            TxtFile.Text = _settings.LastFilePath;
    }

    private void SaveSettings()
    {
        _settings.WindowWidth  = ActualWidth;
        _settings.WindowHeight = ActualHeight;
        _settings.LastOp       = _currentOp.ToString();
        _settings.LastFilePath = TxtFile?.Text.Trim() ?? "";
        if (CboVendor.SelectedItem is string v) _settings.LastVendor = v;
        SettingsManager.Save(_settings);
    }

    private string? _pendingGwZipUrl;
    private string? _pendingAppExeUrl;

    private async Task CheckForUpdatesAsync(string gwCurrentVer)
    {
        try
        {
            var appRelease = await UpdateChecker.GetLatestReleaseAsync("ziggystar12", "HamsterWeazle");
            var gwRelease  = await UpdateChecker.GetLatestReleaseAsync("keirf", "greaseweazle");

            string appCurrent = UpdateChecker.CurrentAppVersion();
            bool appNewer = appRelease != null && UpdateChecker.IsNewer(appRelease.TagName, appCurrent);
            bool gwNewer  = gwRelease  != null && !string.IsNullOrEmpty(gwCurrentVer)
                            && UpdateChecker.IsNewer(gwRelease.TagName, gwCurrentVer);

            if (!appNewer && !gwNewer) return;

            var msg = new System.Text.StringBuilder();
            if (appNewer)
            {
                msg.Append(string.Concat("HamsterWeazle ", appRelease!.TagName, " available"));
                _pendingAppExeUrl = appRelease.DownloadUrl;
            }
            if (gwNewer)
            {
                if (msg.Length > 0) msg.Append("  |  ");
                msg.Append(string.Concat("gw.exe ", gwRelease!.TagName, " available"));
                _pendingGwZipUrl = gwRelease.DownloadUrl;
            }

            _ = Dispatcher.InvokeAsync(() =>
            {
                TxtUpdateMsg.Text     = msg.ToString();
                UpdateBanner.Visibility = Visibility.Visible;
            });
        }
        catch { }
    }

    private async void BtnDoUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnDoUpdate.IsEnabled = false;
        TxtUpdateMsg.Text = "Downloading...";
        string tmp = Path.GetTempPath();

        try
        {
            if (!string.IsNullOrEmpty(_pendingGwZipUrl) && !string.IsNullOrEmpty(_runner.GwPath))
            {
                string gwDir   = Path.GetDirectoryName(_runner.GwPath)!;
                string zipPath = Path.Combine(tmp, "gw_update.zip");
                AppendLog("[update] Downloading gw.exe update...");
                await UpdateChecker.DownloadAsync(_pendingGwZipUrl, zipPath);
                AppendLog("[update] Extracting...");
                await UpdateChecker.InstallGwFromZip(zipPath, gwDir);
                File.Delete(zipPath);
                string ver = await GwRunner.GetVersionAsync(_runner.GwPath);
                AppendLog(string.Concat("[update] gw.exe updated to ", ver));
                _pendingGwZipUrl = null;
            }

            if (!string.IsNullOrEmpty(_pendingAppExeUrl))
            {
                string newExe = Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.new.exe");
                AppendLog("[update] Downloading HamsterWeazle update...");
                await UpdateChecker.DownloadAsync(_pendingAppExeUrl, newExe);
                AppendLog("[update] Launching updater and restarting...");
                UpdateChecker.LaunchSelfUpdateScript(newExe, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.exe"));
                Application.Current.Shutdown();
                return;
            }

            UpdateBanner.Visibility = Visibility.Collapsed;
            TxtUpdateMsg.Text       = "";
        }
        catch (Exception ex)
        {
            TxtUpdateMsg.Text     = string.Concat("Update failed: ", ex.Message);
            BtnDoUpdate.IsEnabled = true;
        }
    }

    private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e)
    { UpdateBanner.Visibility = Visibility.Collapsed; }
}
