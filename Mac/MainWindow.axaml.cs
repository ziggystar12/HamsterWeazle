using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using HamsterWeazle.Models;
using HamsterWeazle.Services;

namespace HamsterWeazle;

public partial class MainWindow : Window
{
    private readonly GwRunner _runner    = new();
    private readonly GwRunner _hxcRunner = new();
    private AppSettings _settings;
    private IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> _allFormats = [];
    private GwOperation _currentOp = GwOperation.Read;
    private readonly StringBuilder _logBuf = new();

    public MainWindow()
    {
        _settings = SettingsManager.Load();
        InitializeComponent();
        Width  = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _runner.OutputReceived    += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _runner.ProcessExited     += code => Dispatcher.UIThread.Post(() => OnProcessDone(code));
        _hxcRunner.OutputReceived += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        Loaded += async (_, _) =>
        {
            TxtTitleVersion.Text = string.Concat(" v", UpdateChecker.CurrentAppVersion());
            PopulateDevicePorts();
            LoadFormats(); RestoreLastOp(); await DetectGwAsync(); await DetectHxcAsync();
            UpdateTabUI(); RestoreLastFilePath(); UpdateCommandPreview(); RefreshSidebar();
        };
        Closing += (_, _) => SaveSettings();
    }

    private T? Res<T>(string key) where T : class
    {
        Application.Current!.TryGetResource(key, null, out var v);
        return v as T;
    }

    // ─── Formats ────────────────────────────────────────────────────────────────
    private void LoadFormats()
    {
        try { _allFormats = CfgParser.Parse(); }
        catch (Exception ex) { AppendLog(string.Concat("[warn] diskdefs: ", ex.Message)); }
        CboVendor.Items.Clear();
        foreach (var (v, _) in _allFormats) CboVendor.Items.Add(v);
        int idx = IndexOf(CboVendor, _settings.LastVendor);
        CboVendor.SelectedIndex = idx >= 0 ? idx : (CboVendor.Items.Count > 0 ? 0 : -1);
    }

    private static int IndexOf(ComboBox cbo, string? value)
    {
        for (int i = 0; i < cbo.Items.Count; i++)
            if (cbo.Items[i]?.ToString() == value) return i;
        return -1;
    }

    // ─── gw / HxC detection ─────────────────────────────────────────────────────
    private async Task DetectGwAsync()
    {
        string? path = _settings.GwPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = GwRunner.FindGwExe();
        if (path != null)
        {
            _runner.GwPath = path; _settings.GwPath = path;
            string ver = await GwRunner.GetVersionAsync(path);
            _ = CheckForUpdatesAsync(ver);
        }
        else
        {
            var dlg = new SetupDialog();
            await dlg.ShowDialog(this);
            if (!string.IsNullOrEmpty(dlg.GwExePath))
            {
                _runner.GwPath = dlg.GwExePath; _settings.GwPath = dlg.GwExePath;
                SettingsManager.Save(_settings);
                string ver = await GwRunner.GetVersionAsync(dlg.GwExePath);
                _ = CheckForUpdatesAsync(ver);
            }
            else { _ = CheckForUpdatesAsync(""); }
        }
    }

    private async Task DetectHxcAsync()
    {
        string? path = _settings.HxcPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = UpdateChecker.FindHxcGuiExe();

        if (path != null)
        {
            _settings.HxcPath = path;
            _settings.HxcSetupOffered = true;
            string? cli = UpdateChecker.FindHxcCliExe(Path.GetDirectoryName(path));
            if (cli != null) _hxcRunner.GwPath = cli;
        }
        else if (!_settings.HxcSetupOffered && !string.IsNullOrEmpty(_runner.GwPath))
        {
            // gw is installed but HxC has never been offered — show the HxC install step
            _settings.HxcSetupOffered = true;
            SettingsManager.Save(_settings);
            var dlg = new SetupDialog(hxcOnly: true);
            await dlg.ShowDialog(this);
            // Reload in case the dialog saved a new HxC path
            _settings = SettingsManager.Load();
            if (!string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath))
            {
                string? cli = UpdateChecker.FindHxcCliExe(Path.GetDirectoryName(_settings.HxcPath));
                if (cli != null) _hxcRunner.GwPath = cli;
            }
        }
    }

    // ─── Window drag ─────────────────────────────────────────────────────────────
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ─── Op tabs ─────────────────────────────────────────────────────────────────
    private void OpTab_Click(object? sender, RoutedEventArgs e)
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
        PanelFormat.IsVisible = rw;
        PanelErase.IsVisible  = erase;
        PanelTools.IsVisible  = tools;
        AdvExpander.IsVisible = rw;
        PanelRevs.IsVisible   = _currentOp == GwOperation.Read;
        BtnRun.IsVisible      = !tools;
        BtnCancel.IsVisible   = !tools;
        BtnAutoRead.IsVisible = _currentOp == GwOperation.Read;
        BtnRun.Content = _currentOp switch
        {
            GwOperation.Read  => "  READ  ",
            GwOperation.Write => "  WRITE  ",
            GwOperation.Erase => "  ERASE  ",
            _                 => "  RUN  ",
        };
        LblFile.Content = _currentOp == GwOperation.Read ? "Save to:" : "Image file:";
        TxtAutoDetect.IsVisible = false;
        bool customOut = ChkCustomOutput?.IsChecked == true;
        PanelFile.IsVisible = _currentOp == GwOperation.Write ||
                              (_currentOp == GwOperation.Read && customOut);
        if (_currentOp == GwOperation.Read && !customOut)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            TxtFile.Text = Path.Combine(GetInboxDir(), string.Concat("disk_", ts, ".img"));
        }
    }

    private void ChkCustomOutput_Changed(object? sender, RoutedEventArgs e) => UpdateTabUI();

    // ─── Format combos ───────────────────────────────────────────────────────────
    private void CboVendor_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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

    private void CboFormat_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboFormat.SelectedItem is DiskFormat fmt) _settings.LastFormat = fmt.FullName;
        UpdateCommandPreview();
    }

    // ─── File browse ─────────────────────────────────────────────────────────────
    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var imgTypes = new FilePickerFileType("Disk images")
            { Patterns = ["*.img", "*.hfe", "*.scp", "*.adf", "*.ima"] };
        var allFiles = new FilePickerFileType("All files") { Patterns = ["*"] };

        if (_currentOp == GwOperation.Read)
        {
            var opts = new FilePickerSaveOptions
            {
                Title = "Save disk image as...",
                SuggestedFileName = Path.GetFileName(TxtFile.Text ?? ""),
                FileTypeChoices = [imgTypes, allFiles],
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(GetInboxDir()),
            };
            var result = await StorageProvider.SaveFilePickerAsync(opts);
            if (result != null)
            {
                TxtFile.Text = result.Path.LocalPath;
                _settings.LastOutputDir = Path.GetDirectoryName(result.Path.LocalPath) ?? "";
            }
        }
        else
        {
            string dir = string.IsNullOrEmpty(_settings.LastOutputDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _settings.LastOutputDir;
            var opts = new FilePickerOpenOptions
            {
                Title = "Open disk image...", AllowMultiple = false,
                FileTypeFilter = [imgTypes, allFiles],
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir),
            };
            var files = await StorageProvider.OpenFilePickerAsync(opts);
            if (files.Count > 0)
            {
                TxtFile.Text = files[0].Path.LocalPath;
                _settings.LastOutputDir = Path.GetDirectoryName(files[0].Path.LocalPath) ?? "";
            }
        }
        UpdateCommandPreview();
    }

    // ─── Options changed ─────────────────────────────────────────────────────────
    private void Options_Changed(object? sender, RoutedEventArgs e) => UpdateCommandPreview();

    private void TxtFile_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_currentOp == GwOperation.Write)
            TryAutoDetectFormat(TxtFile.Text?.Trim() ?? "");
        UpdateCommandPreview();
    }

    private void Options_TextChanged(object? sender, TextChangedEventArgs e) => UpdateCommandPreview();

    private void TryAutoDetectFormat(string path)
    {
        string? fullName = FormatGuesser.Guess(path);
        if (fullName == null) return;
        string[] parts = fullName.Split('.');
        if (parts.Length < 2) return;
        string vendorKey = parts[0];
        var entry = _allFormats.FirstOrDefault(x =>
            x.Formats.Any(f => f.FullName.StartsWith(vendorKey + ".", StringComparison.OrdinalIgnoreCase)));
        if (entry == default) return;
        var fmt = entry.Formats.FirstOrDefault(f => f.FullName == fullName);
        if (fmt == null) return;
        if (CboVendor.SelectedItem?.ToString() != entry.Vendor)
            CboVendor.SelectedItem = entry.Vendor;
        if (CboFormat.SelectedItem is not DiskFormat cur || cur.FullName != fullName)
        {
            CboFormat.SelectedItem  = fmt;
            TxtAutoDetect.IsVisible = true;
            TxtAutoDetect.Text      = string.Concat("Auto-detected: ", fullName);
        }
    }

    // ─── Command preview ─────────────────────────────────────────────────────────
    private void UpdateCommandPreview()
    {
        if (TxtCmdPreview == null) return;
        string format   = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string filePath = TxtFile?.Text?.Trim() ?? "";
        if (_currentOp is GwOperation.Read or GwOperation.Write
            && (string.IsNullOrEmpty(format) || string.IsNullOrEmpty(filePath)))
        {
            TxtCmdPreview.Text = "(select format and file to preview the command)";
            return;
        }
        string args   = _runner.BuildArguments(_currentOp, format, filePath, BuildCurrentOptions());
        string gwName = string.IsNullOrEmpty(_runner.GwPath) ? "gw" : Path.GetFileName(_runner.GwPath);
        TxtCmdPreview.Text = string.Concat(gwName, " ", args);
    }

    private GwOptions BuildCurrentOptions()
    {
        int.TryParse(TxtCylStart?.Text, out int s);
        int.TryParse(TxtCylEnd?.Text,   out int e2);
        int.TryParse(TxtRetries?.Text,  out int r);
        if (r == 0) r = 3;
        bool range  = s != 0 || e2 != 79;
        string? drive  = RbDriveA?.IsChecked == true ? "0" : RbDriveB?.IsChecked == true ? "1" : null;
        string? device = string.IsNullOrEmpty(_settings.DevicePort) ? null : _settings.DevicePort;
        int.TryParse(TxtRevs?.Text, out int revs);
        return new GwOptions(StartCyl: range ? s : null, EndCyl: range ? e2 : null,
                             Retries: r, Verify: ChkVerify?.IsChecked == true,
                             Drive: drive, Revs: revs > 1 ? revs : null,
                             DevicePort: device);
    }

    // ─── Run / Cancel ─────────────────────────────────────────────────────────────
    private async void BtnRun_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw not configured."); return; }
        string format   = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string filePath = TxtFile.Text?.Trim() ?? "";
        string args     = _runner.BuildArguments(_currentOp, format, filePath, BuildCurrentOptions());
        if (_currentOp == GwOperation.Write) PushToWriteQueue(filePath, format);
        SetRunning(true);
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private async void QuickWrite_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WriteQueueItem item) return;
        if (!item.FileExists) { AppendLog(string.Concat("[error] File not found: ", item.FilePath)); return; }
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        string args = _runner.BuildArguments(GwOperation.Write, item.Format, item.FilePath, new GwOptions());
        PushToWriteQueue(item.FilePath, item.Format);
        SetRunning(true);
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    { _autoCts?.Cancel(); _runner.Cancel(); AppendLog("[cancelling...]"); }

    private void SetRunning(bool running) => Dispatcher.UIThread.Post(() =>
    {
        BtnRun.IsEnabled = !running; BtnCancel.IsEnabled = running; ProgressBar.IsVisible = running;
    });

    private void OnProcessDone(int code)
    {
        AppendLog(string.Concat(Environment.NewLine, "[exit code ", code, "]"));
        SetRunning(false);
        if (_currentOp == GwOperation.Read) RefreshInbox();
    }

    private void AppendLog(string line)
    {
        _logBuf.AppendLine(line);
        TxtLog.Text = _logBuf.ToString();
        Dispatcher.UIThread.Post(() =>
            LogScroll.Offset = new Vector(LogScroll.Offset.X, double.MaxValue),
            DispatcherPriority.Background);
    }

    private void BtnClearLog_Click(object? sender, RoutedEventArgs e)
    { _logBuf.Clear(); TxtLog.Text = ""; }

    private string GetInboxDir()
    {
        string dir = string.IsNullOrEmpty(_settings.InboxDir)
            ? RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(AppContext.BaseDirectory, "inbox")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HamsterWeazle")
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
            var tb = new TextBlock { Text = "No writes yet.", Margin = new Thickness(10, 8, 10, 0) };
            if (Res<IBrush>("Win.SubText") is { } b) tb.Foreground = b;
            WriteQueuePanel.Children.Add(tb); return;
        }
        foreach (var item in _settings.WriteQueueItems)
            WriteQueuePanel.Children.Add(BuildQueueCard(item));
    }

    private Control BuildQueueCard(WriteQueueItem item)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        if (Res<IBrush>("Win.Panel2") is { } bg) border.Background = bg;
        var sp  = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var fmtTxt = new TextBlock { Text = item.Format, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis };
        if (Res<IBrush>("Win.Accent") is { } ac) fmtTxt.Foreground = ac;
        Grid.SetColumn(fmtTxt, 0);
        var runBtn = new Button { Content = "Run", Tag = item,
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = item.FileExists };
        runBtn.Classes.Add("primary"); runBtn.Click += QuickWrite_Click;
        Grid.SetColumn(runBtn, 1);
        row.Children.Add(fmtTxt); row.Children.Add(runBtn);
        var nameTxt = new TextBlock { Text = item.FileName,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        if (Res<IBrush>(item.FileExists ? "Win.Text" : "Win.Error") is { } eb) nameTxt.Foreground = eb;
        var pathTxt = new TextBlock { Text = item.ShortPath, TextTrimming = TextTrimming.CharacterEllipsis };
        if (Res<IBrush>("Win.SubText") is { } s1) pathTxt.Foreground = s1;
        var dateTxt = new TextBlock { Text = item.DateLabel };
        if (Res<IBrush>("Win.SubText") is { } s2) dateTxt.Foreground = s2;
        var hxcRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var listBtn = new Button { Content = "List Files", Tag = item.FilePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = item.FileExists };
        listBtn.Classes.Add("primary"); listBtn.Click += HxcList_Click;
        var openBtn = new Button { Content = "Open HxC", Tag = item.FilePath,
            Padding = new Thickness(6, 2, 6, 2), Height = 22, FontSize = 10,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = item.FileExists && !string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath) };
        openBtn.Classes.Add("ghost"); openBtn.Click += HxcOpen_Click;
        hxcRow.Children.Add(listBtn); hxcRow.Children.Add(openBtn);
        sp.Children.Add(row); sp.Children.Add(nameTxt); sp.Children.Add(pathTxt);
        sp.Children.Add(dateTxt); sp.Children.Add(hxcRow);
        border.Child = sp; return border;
    }

    private void RefreshInbox()
    {
        if (InboxPanel == null) return;
        InboxPanel.Children.Clear();
        string dir = GetInboxDir();
        var files = Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTime).Take(50).ToList();
        if (files.Count == 0)
        {
            var tb = new TextBlock { Text = "No images yet.", Margin = new Thickness(10, 8, 10, 0) };
            if (Res<IBrush>("Win.SubText") is { } b) tb.Foreground = b;
            InboxPanel.Children.Add(tb); return;
        }
        foreach (string f in files) InboxPanel.Children.Add(BuildInboxCard(f));
    }

    private Control BuildInboxCard(string filePath)
    {
        var fi  = new FileInfo(filePath);
        double mb = fi.Length / (1024.0 * 1024.0);
        string size = mb >= 1.0 ? string.Concat(mb.ToString("F1"), " MB") : string.Concat((fi.Length / 1024.0).ToString("F0"), " KB");

        string? fmtCode = GetFormatCodeForFile(filePath);
        string  fmtLabel = FormatLabel(fmtCode);

        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        if (Res<IBrush>("Win.Panel2") is { } bg) border.Background = bg;
        var sp = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };

        // ── top row: filename (or edit box) + size ──────────────────
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var nameTxt = new TextBlock { Text = fi.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = new Cursor(StandardCursorType.Hand) };
        if (Res<IBrush>("Win.Text") is { } t) nameTxt.Foreground = t;
        nameTxt.Tapped += (_, _) => { TxtFile.Text = filePath; TabWrite.IsChecked = true; OpTab_Click(TabWrite, new RoutedEventArgs()); };
        Grid.SetColumn(nameTxt, 0);

        var nameEdit = new TextBox { IsVisible = false, Text = Path.GetFileNameWithoutExtension(fi.Name),
            Height = 22, FontSize = 11, Padding = new Thickness(4, 0) };
        Grid.SetColumn(nameEdit, 0);

        var sizeTxt = new TextBlock { Text = size, Margin = new Thickness(8, 0, 0, 0) };
        if (Res<IBrush>("Win.SubText") is { } s) sizeTxt.Foreground = s;
        Grid.SetColumn(sizeTxt, 1);
        topRow.Children.Add(nameTxt); topRow.Children.Add(nameEdit); topRow.Children.Add(sizeTxt);

        // ── format label ────────────────────────────────────────────
        var fmtTxt = new TextBlock { Text = fmtLabel, IsVisible = !string.IsNullOrEmpty(fmtLabel),
            FontSize = 10, Margin = new Thickness(0, 1, 0, 0) };
        if (Res<IBrush>("Win.SubText") is { } sf) fmtTxt.Foreground = sf;

        // ── button row ───────────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

        var listBtn = new Button { Content = "List Files", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand) };
        listBtn.Classes.Add("primary"); listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "Open HxC", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = !string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath) };
        openBtn.Classes.Add("ghost"); openBtn.Click += HxcOpen_Click;

        var writeBtn = new Button { Content = "Write", Tag = new[] { filePath, fmtCode ?? "" },
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand),
            IsEnabled = !string.IsNullOrEmpty(_runner.GwPath) };
        writeBtn.Classes.Add("primary");
        writeBtn.Click += async (_, _) =>
        {
            if (sender_writeBtn_tag(writeBtn) is not (string path, string fmt)) return;
            // If no known format, pre-populate Write tab and let user choose
            if (string.IsNullOrEmpty(fmt))
            { TxtFile.Text = path; TabWrite.IsChecked = true; OpTab_Click(TabWrite, new RoutedEventArgs()); return; }
            await QuickWriteFromInbox(path, fmt);
        };

        var renameBtn = new Button { Content = "Rename",
            Padding = new Thickness(6, 2, 6, 2), Height = 22, FontSize = 10,
            Cursor = new Cursor(StandardCursorType.Hand) };
        renameBtn.Classes.Add("ghost");
        renameBtn.Click += (_, _) =>
        {
            nameEdit.Text = Path.GetFileNameWithoutExtension(fi.Name);
            nameTxt.IsVisible = false; sizeTxt.IsVisible = false; renameBtn.IsVisible = false;
            nameEdit.IsVisible = true;
            nameEdit.Focus(); nameEdit.SelectAll();
        };

        void CommitRename()
        {
            string newStem = (nameEdit.Text ?? "").Trim();
            nameEdit.IsVisible = false; nameTxt.IsVisible = true;
            sizeTxt.IsVisible = true; renameBtn.IsVisible = true;
            if (string.IsNullOrEmpty(newStem)) return;
            string newPath = Path.Combine(fi.DirectoryName!, newStem + fi.Extension);
            if (newPath == filePath) return;
            try { File.Move(filePath, newPath, overwrite: false); RefreshInbox(); }
            catch { }
        }
        nameEdit.KeyDown += (_, e) => { if (e.Key == Key.Return) CommitRename(); else if (e.Key == Key.Escape) { nameEdit.IsVisible = false; nameTxt.IsVisible = true; sizeTxt.IsVisible = true; renameBtn.IsVisible = true; } };
        nameEdit.LostFocus += (_, _) => { if (nameEdit.IsVisible) CommitRename(); };

        btnRow.Children.Add(listBtn); btnRow.Children.Add(openBtn);
        btnRow.Children.Add(writeBtn); btnRow.Children.Add(renameBtn);
        sp.Children.Add(topRow); sp.Children.Add(fmtTxt); sp.Children.Add(btnRow);
        border.Child = sp; return border;
    }

    private static (string, string)? sender_writeBtn_tag(Button btn) =>
        btn.Tag is string[] arr && arr.Length == 2 ? (arr[0], arr[1]) : null;

    private string? GetFormatCodeForFile(string filePath)
    {
        string? code = FormatGuesser.Guess(filePath);
        if (code != null) return code;
        return _settings.WriteQueueItems
            .FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            ?.Format;
    }

    private string FormatLabel(string? fmtCode)
    {
        if (fmtCode == null) return "";
        foreach (var (vendor, fmts) in _allFormats)
        {
            var f = fmts.FirstOrDefault(x => x.FullName == fmtCode);
            if (f != null) return string.Concat(vendor, " · ", f.ShortName);
        }
        return fmtCode;
    }

    private async Task QuickWriteFromInbox(string filePath, string fmtCode)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        string args = _runner.BuildArguments(GwOperation.Write, fmtCode, filePath, BuildCurrentOptions());
        PushToWriteQueue(filePath, fmtCode);
        SetRunning(true);
        try   { await _runner.RunAsync(args); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    // ─── Auto-read ────────────────────────────────────────────────────────────────
    private CancellationTokenSource? _autoCts;

    private async void BtnAutoRead_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        string filePath = TxtFile?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var opts = new FilePickerSaveOptions
            {
                Title = "Save disk image as...",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(GetInboxDir()),
            };
            var result = await StorageProvider.SaveFilePickerAsync(opts);
            if (result == null) return;
            filePath = result.Path.LocalPath;
            if (TxtFile != null) TxtFile.Text = filePath;
            _settings.LastOutputDir = Path.GetDirectoryName(filePath) ?? "";
        }
        var candidates = new[]
        {
            ("ibm.1440",           "IBM PC 1.44 MB HD"),
            ("ibm.720",            "IBM PC 720 KB DD"),
            ("ibm.1200",           "IBM PC 1.2 MB HD 5.25\""),
            ("ibm.360",            "IBM PC 360 KB DD 5.25\""),
            ("amiga.amigados",     "Amiga 880 KB DD"),
            ("amiga.amigados-hd",  "Amiga 1.76 MB HD"),
            ("atarist.720",        "Atari ST 720 KB DS"),
            ("atarist.360",        "Atari ST 360 KB SS"),
            ("atarist.1440",       "Atari ST 1.44 MB HD"),
        };
        SetRunning(true); _autoCts = new CancellationTokenSource();
        AppendLog("[auto read] Probing format..."); AppendLog("");
        string? bestFmt = null; string? bestName = null; int bestScore = -1;
        string tmpProbe = Path.Combine(Path.GetTempPath(), "hw_probe.img");
        // Include device port if user has one configured
        string deviceArg = string.IsNullOrEmpty(_settings.DevicePort) ? "" : $" --device {_settings.DevicePort}";

        foreach (var (fmt, name) in candidates)
        {
            string probeArgs = $"read --format {fmt} --tracks c=0-1 --retries 0{deviceArg} \"{tmpProbe}\"";
            var psi = new ProcessStartInfo(_runner.GwPath!, probeArgs)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            // Read both streams in parallel to avoid deadlock, then wait for exit
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(_autoCts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } break; }
            string raw = (await stdoutTask) + (await stderrTask);
            int ok = 0, bad = 0;
            foreach (var ln in raw.Replace("\r", "").Split('\n'))
            {
                var m2 = Regex.Match(ln, @"\b(\d+)/(\d+)\b");
                if (m2.Success && int.TryParse(m2.Groups[2].Value, out int den) && den >= 8)
                {
                    int num = int.Parse(m2.Groups[1].Value);
                    if (num * 2 >= den) ok++; else bad++;
                }
                else if (ln.Contains("No sector", StringComparison.OrdinalIgnoreCase) ||
                         ln.Contains("unrecoverable", StringComparison.OrdinalIgnoreCase) ||
                         ln.Contains("CRC error",    StringComparison.OrdinalIgnoreCase))
                {
                    bad++;
                }
            }
            int score = ok - bad;
            AppendLog(string.Concat("  ", name.PadRight(26), " score:", score >= 0 ? "+" : "", score, "  (", ok, " OK, ", bad, " errors)"));
            if (score > bestScore) { bestScore = score; bestFmt = fmt; bestName = name; }
        }
        try { if (File.Exists(tmpProbe)) File.Delete(tmpProbe); } catch { }
        if (bestFmt == null || bestScore < 0)
        { AppendLog("[auto read] Could not identify format. Try selecting manually and using READ."); SetRunning(false); return; }
        AppendLog(string.Concat(Environment.NewLine, "[auto read] Best match: ", bestName, " (", bestFmt, ")  starting full read..."));
        AppendLog("");
        var entry = _allFormats.FirstOrDefault(x => x.Formats.Any(f => f.FullName == bestFmt));
        if (entry != default)
        {
            CboVendor.SelectedItem = entry.Vendor;
            var fmt2 = entry.Formats.FirstOrDefault(f => f.FullName == bestFmt);
            if (fmt2 != null) CboFormat.SelectedItem = fmt2;
        }
        string fullArgs = _runner.BuildArguments(GwOperation.Read, bestFmt, filePath, BuildCurrentOptions());
        try   { await _runner.RunAsync(fullArgs); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void BtnOpenInbox_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "explorer.exe" : "open", GetInboxDir()); }
        catch { }
    }

    private async void HxcList_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        string? hintDir = !string.IsNullOrEmpty(_settings.HxcPath) ? Path.GetDirectoryName(_settings.HxcPath) : null;
        string? cli = _hxcRunner.GwPath ?? UpdateChecker.FindHxcCliExe(hintDir);
        if (string.IsNullOrEmpty(cli)) { AppendLog("[error] hxcfe not found."); return; }
        _hxcRunner.GwPath = cli;

        // Non-HFE formats must be converted first
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string inputPath = filePath;
        string? tmpHfe = null;
        if (ext is ".img" or ".ima" or ".dsk" or ".st")
        {
            tmpHfe = Path.Combine(Path.GetTempPath(), "hw_hxc_list.hfe");
            AppendLog(string.Concat("$ hxcfe -conv:HXC_HFE -finput:\"", filePath, "\" -foutput:\"", tmpHfe, "\""));
            AppendLog("");
            try { await _hxcRunner.RunAsync(string.Concat("-conv:HXC_HFE -finput:\"", filePath, "\" -foutput:\"", tmpHfe, "\"")); }
            catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); return; }
            if (!File.Exists(tmpHfe) || new FileInfo(tmpHfe).Length == 0)
            { AppendLog("[error] Format conversion to HFE failed."); return; }
            inputPath = tmpHfe;
            AppendLog("");
        }

        AppendLog(string.Concat("$ hxcfe -list -finput:\"", inputPath, "\""));
        AppendLog("");
        try { await _hxcRunner.RunAsync(string.Concat("-list -finput:\"", inputPath, "\"")); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { if (tmpHfe != null) try { File.Delete(tmpHfe); } catch { } }
    }

    private void HxcOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        string? gui = _settings.HxcPath ?? UpdateChecker.FindHxcGuiExe();
        if (string.IsNullOrEmpty(gui)) { AppendLog("[error] HxCFloppyEmulator not found."); return; }
        try { Process.Start(new ProcessStartInfo(gui, string.Concat("\"", filePath, "\"")) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
    }

    private async void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        await new SettingsDialog().ShowDialog(this);
        _settings = SettingsManager.Load();
        await DetectHxcAsync();
        RefreshSidebar();
    }

    private async void BtnUpdateFirmware_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        SetRunning(true); AppendLog("$ gw update"); AppendLog("");
        try   { await _runner.RunAsync("update"); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private async void BtnCleanDrive_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        string driveArg = RbDriveB?.IsChecked == true ? " --drive 1" : "";
        SetRunning(true);
        AppendLog(string.Concat("$ gw clean", driveArg));
        AppendLog("Insert cleaning disk now and ensure drive motor is running.");
        try   { await _runner.RunAsync(string.Concat("clean", driveArg)); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private void PopulateDevicePorts()
    {
        CboDevice.Items.Clear();
        CboDevice.Items.Add("Auto");
        try { foreach (string port in System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p)) CboDevice.Items.Add(port); }
        catch { }
        string saved = _settings.DevicePort ?? "";
        if (string.IsNullOrEmpty(saved)) { CboDevice.SelectedIndex = 0; return; }
        for (int i = 0; i < CboDevice.Items.Count; i++)
            if (CboDevice.Items[i]?.ToString() == saved) { CboDevice.SelectedIndex = i; return; }
        CboDevice.SelectedIndex = 0;
    }

    private void CboDevice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboDevice.SelectedItem is not string sel) return;
        _settings.DevicePort = sel == "Auto" ? "" : sel;
        UpdateCommandPreview();
    }

    private void RestoreLastOp()
    {
        _currentOp = _settings.LastOp switch
        {
            "Write" => GwOperation.Write, "Erase" => GwOperation.Erase,
            "Tools" => GwOperation.Tools, "Info"  => GwOperation.Info,
            _       => GwOperation.Read,
        };
        foreach (var t in new[] { TabRead, TabWrite, TabErase, TabTools, TabInfo })
            t.IsChecked = t.Tag?.ToString() == _settings.LastOp;
        if (_currentOp == GwOperation.Read) TabRead.IsChecked = true;
    }

    private void RestoreLastFilePath()
    {
        if (!string.IsNullOrEmpty(_settings.LastFilePath)) TxtFile.Text = _settings.LastFilePath;
    }

    private void SaveSettings()
    {
        _settings.WindowWidth  = ClientSize.Width;
        _settings.WindowHeight = ClientSize.Height;
        _settings.LastOp       = _currentOp.ToString();
        _settings.LastFilePath = TxtFile?.Text?.Trim() ?? "";
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
            var msg = new StringBuilder();
            if (appNewer) { msg.Append(string.Concat("HamsterWeazle ", appRelease!.TagName, " available")); _pendingAppExeUrl = appRelease.DownloadUrl; }
            if (gwNewer)  { if (msg.Length > 0) msg.Append("  |  "); msg.Append(string.Concat("gw ", gwRelease!.TagName, " available")); _pendingGwZipUrl = gwRelease.DownloadUrl; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            { TxtUpdateMsg.Text = msg.ToString(); UpdateBanner.IsVisible = true; });
        }
        catch { }
    }

    private async void BtnDoUpdate_Click(object? sender, RoutedEventArgs e)
    {
        BtnDoUpdate.IsEnabled = false; TxtUpdateMsg.Text = "Downloading...";
        string tmp = Path.GetTempPath();
        try
        {
            if (!string.IsNullOrEmpty(_pendingGwZipUrl) && !string.IsNullOrEmpty(_runner.GwPath))
            {
                string gwDir = Path.GetDirectoryName(_runner.GwPath)!;
                string zip   = Path.Combine(tmp, "gw_update.zip");
                AppendLog("[update] Downloading gw update...");
                await UpdateChecker.DownloadAsync(_pendingGwZipUrl, zip);
                AppendLog("[update] Extracting...");
                await UpdateChecker.InstallGwFromZip(zip, gwDir);
                AppendLog("[update] gw updated. Restart to apply."); _pendingGwZipUrl = null;
            }
            if (!string.IsNullOrEmpty(_pendingAppExeUrl))
            {
                string newExe = Path.Combine(AppContext.BaseDirectory, "HamsterWeazle.new");
                AppendLog("[update] Downloading HamsterWeazle update...");
                await UpdateChecker.DownloadAsync(_pendingAppExeUrl, newExe);
                UpdateChecker.LaunchSelfUpdateScript(newExe,
                    Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle"));
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                return;
            }
            UpdateBanner.IsVisible = false; TxtUpdateMsg.Text = "";
        }
        catch (Exception ex)
        { TxtUpdateMsg.Text = string.Concat("Update failed: ", ex.Message); BtnDoUpdate.IsEnabled = true; }
    }

    private void BtnDismissUpdate_Click(object? sender, RoutedEventArgs e)
    { UpdateBanner.IsVisible = false; }
}
