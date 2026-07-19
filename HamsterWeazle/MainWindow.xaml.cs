using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HamsterWeazle.Models;
using HamsterWeazle.Services;
using Microsoft.Win32;

namespace HamsterWeazle;

public partial class MainWindow : Window
{
    private readonly GwRunner _runner = new();
    private readonly GwRunner _hxcRunner = new();
    private AppSettings _settings;
    private string? _detectedDriveFamily; // cached for session: "3.5", "5.25", or null
    private IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> _allFormats = [];
    private GwOperation _currentOp = GwOperation.Read;
    private bool _refreshingPresets;
    private bool _initialisingUi = true;
    private readonly DispatcherTimer _driveLedTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private bool _driveLedOn;
    private bool _driveIsRunning;
    private bool _driveHasError;
    private bool _driveErrorFlashes;
    private int? _lastFailedVerifyCyl;
    private bool _deferProcessDone;
    private bool _suppressExpectedEraseVerifyReadIssues;

    private const int MaxAdaptiveWriteResumePasses = 3;
    private static readonly Regex FailedVerifyTrackRegex = new(
        @"Failed to verify Track\s+(\d+)(?:\.(\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GreaseweazleTrackStatusRegex = new(
        @"^\s*T\d+(?:\.\d+)?:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GreaseweazleTrackNumberRegex = new(
        @"^\s*T(?<compact>\d+)(?:\.\d+)?:|\b(?:track|cyl(?:inder)?)\s*[=:]?\s*(?<word>\d+)(?:\.\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string Wc = ((char)42).ToString();
    private static readonly string ExeFilter  = string.Concat("gw.exe|gw.exe|Executables|",Wc,".exe");

    public MainWindow()
    {
        _settings = SettingsManager.Load();
        App.SwitchTheme(_settings.Theme);
        InitializeComponent();
        Width  = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _driveLedTimer.Tick += (_, _) =>
        {
            _driveLedOn = !_driveLedOn;
            UpdateDriveLed();
        };
        _runner.OutputReceived += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        _runner.ProcessExited  += code =>
        {
            bool defer = _deferProcessDone;
            Dispatcher.InvokeAsync(() =>
            {
                if (!defer)
                    OnProcessDone(code);
            });
        };
        _hxcRunner.OutputReceived += line => Dispatcher.InvokeAsync(() => AppendLog(line));
        Loaded  += async (_, _) =>
        {
            TxtTitleVersion.Text = string.Concat("v", UpdateChecker.CurrentAppVersion());
            WhatsNewDot.Visibility = _settings.SeenWhatsNewVersion == UpdateChecker.CurrentAppVersion()
                ? Visibility.Collapsed : Visibility.Visible;
            PopulateDevicePorts();
            if (ChkAutoDetect != null) ChkAutoDetect.IsChecked = _settings.AutoDetectDriveType;
            LoadFormats(); RefreshPresetList(); RestoreWriteOptions(); RestoreDriveSelection(); RestoreDriveProfile(); RestoreLastOp(); await DetectGwAsync(); await DetectHxcAsync();
            RestoreLastFilePath(); UpdateTabUI(); UpdateCommandPreview(); RefreshSidebar(); UpdateDriveFace();
            _initialisingUi = false;
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

    private async Task DetectHxcAsync()
    {
        string? path = _settings.HxcPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            path = UpdateChecker.FindHxcGuiExe();

        if (path != null)
        {
            _settings.HxcPath = path;
            string? cli = UpdateChecker.FindHxcCliExe();
            if (cli != null) _hxcRunner.GwPath = cli;
            return;
        }

        if (_settings.HxcSetupDeclined) return;

        var dlg = new HxcSetupDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.HxcPath))
        {
            _settings.HxcPath         = dlg.HxcPath;
            _settings.HxcInstalledTag = dlg.InstalledTag ?? "";
            SettingsManager.Save(_settings);
            string? cli = UpdateChecker.FindHxcCliExe();
            if (cli != null) _hxcRunner.GwPath = cli;
            RefreshSidebar();
        }
        else if (dlg.Skipped)
        {
            _settings.HxcSetupDeclined = true;
            SettingsManager.Save(_settings);
        }
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
        PanelFormat.Visibility = rw || erase ? Visibility.Visible : Visibility.Collapsed;
        PanelFile.Visibility   = rw    ? Visibility.Visible : Visibility.Collapsed;
        PanelErase.Visibility  = erase ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility  = tools ? Visibility.Visible : Visibility.Collapsed;
        AdvExpander.Visibility = rw || erase ? Visibility.Visible : Visibility.Collapsed;
        if (PanelRevs != null)
            PanelRevs.Visibility = _currentOp == GwOperation.Read ? Visibility.Visible : Visibility.Collapsed;
        if (ChkCustomOutput != null)
            ChkCustomOutput.Visibility = _currentOp == GwOperation.Read ? Visibility.Visible : Visibility.Collapsed;
        BtnRun.Visibility      = tools ? Visibility.Collapsed : Visibility.Visible;
        BtnEraseVerify.Visibility = erase ? Visibility.Visible : Visibility.Collapsed;
        BtnDetectEraseFormat.Visibility = erase ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility   = tools ? Visibility.Collapsed : Visibility.Visible;
        BtnRun.Content = _currentOp switch
        {
            GwOperation.Read  => "  READ  ",
            GwOperation.Write => "  WRITE  ",
            GwOperation.Erase => "  ERASE  ",
            _                 => "  RUN  ",
        };
        BtnClearLog.Content = ShouldShowCommandOutput() ? "Clear Output" : "Clear Issues";
        if (BtnAutoRead != null)
            BtnAutoRead.Visibility = _currentOp == GwOperation.Read ? Visibility.Visible : Visibility.Collapsed;
        LblFile.Content = _currentOp == GwOperation.Read ? "Save to:" : "Image file:";
        if (TxtAutoDetect != null) TxtAutoDetect.Visibility = Visibility.Collapsed;
        bool customOut = ChkCustomOutput?.IsChecked == true;
        if (PanelFile != null)
            PanelFile.Visibility = _currentOp == GwOperation.Write || (_currentOp == GwOperation.Read && customOut)
                ? Visibility.Visible : Visibility.Collapsed;
        if (_currentOp == GwOperation.Read && !customOut && TxtFile != null)
            TxtFile.Text = GenerateInboxPath();
    }

    private void ChkCustomOutput_Changed(object sender, RoutedEventArgs e) => UpdateTabUI();
    private void ChkAutoDetect_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AutoDetectDriveType = ChkAutoDetect?.IsChecked == true;
        SettingsManager.Save(_settings);
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
        if (_currentOp == GwOperation.Read && ChkCustomOutput?.IsChecked != true && TxtFile != null)
            TxtFile.Text = GenerateInboxPath();
        UpdateCommandPreview();
        UpdateDriveFace();
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        string dir = string.IsNullOrEmpty(_settings.LastOutputDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _settings.LastOutputDir;
        if (_currentOp == GwOperation.Read)
        {
            string? filePath = PickImageSavePath();
            if (filePath != null) TxtFile.Text = filePath;
        }
        else
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open disk image...",
                Filter = DiskImageFileTypes.WindowsOpenFilter(),
                InitialDirectory = dir,
            };
            if (dlg.ShowDialog() == true)
            {
                TxtFile.Text = dlg.FileName;
                _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? "";
                if (_currentOp == GwOperation.Write)
                    TryAutoDetectFormat(dlg.FileName);
            }
        }
        UpdateCommandPreview();
    }

    private string? PickImageSavePath()
    {
        string format = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string preferredExtension = DiskImageFileTypes.PreferredExtension(format);
        string suggestedPath = TxtFile?.Text.Trim() ?? "";
        string suggestedName = string.IsNullOrWhiteSpace(suggestedPath)
            ? "disk"
            : Path.GetFileNameWithoutExtension(suggestedPath);
        var dlg = new SaveFileDialog
        {
            Title = "Save disk image as...",
            Filter = DiskImageFileTypes.WindowsSaveFilter(),
            FilterIndex = DiskImageFileTypes.SaveTypeIndex(preferredExtension) + 1,
            DefaultExt = preferredExtension,
            AddExtension = true,
            InitialDirectory = GetInboxDir(),
            FileName = suggestedName,
        };
        if (dlg.ShowDialog() != true) return null;
        _settings.LastOutputDir = Path.GetDirectoryName(dlg.FileName) ?? "";
        return dlg.FileName;
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialisingUi && sender == CboDriveProfile)
        {
            ApplyDriveProfile(updateDriveSelection: true);
            SaveDriveProfile();
        }
        else if (!_initialisingUi && sender == CboDrive)
        {
            SaveDriveSelection();
        }
        UpdateCommandPreview();
    }
    private void Options_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_initialisingUi && sender == TxtFile && _currentOp == GwOperation.Write)
            TryAutoDetectFormat(TxtFile.Text.Trim());
        UpdateCommandPreview();
    }

    private void TryAutoDetectFormat(string path)
    {
        string? preferredFormat = (CboFormat.SelectedItem as DiskFormat)?.FullName;
        string? fullName = FormatGuesser.Guess(path, preferredFormat);
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
        string? drive  = GetSelectedDriveValue();
        string? device = string.IsNullOrEmpty(_settings.DevicePort) ? null : _settings.DevicePort;
        int.TryParse(TxtRevs?.Text, out int revs);
        return new GwOptions(StartCyl: range ? s : null, EndCyl: range ? e2 : null,
                             Retries: r, Verify: ChkVerify?.IsChecked == true,
                             AdaptiveRetry: ChkAdaptiveRetry?.IsChecked != false,
                             Drive: drive, Revs: revs > 1 ? revs : null,
                             DevicePort: device);
    }

    private void ApplyWriteQueueItemToControls(WriteQueueItem item)
    {
        TxtFile.Text = item.FilePath;
        SelectFormat(item.Format, item.Vendor);
        TxtCylStart.Text = (item.StartCyl ?? 0).ToString();
        TxtCylEnd.Text   = (item.EndCyl ?? 79).ToString();
        TxtRetries.Text  = (item.Retries > 0 ? item.Retries : 3).ToString();
        ChkVerify.IsChecked = item.Verify;
        ChkAdaptiveRetry.IsChecked = item.AdaptiveRetry;
        ApplyDriveSelection(item.Drive, item.DriveName);
        if (TxtRevs != null)
            TxtRevs.Text = (item.Revs ?? 1).ToString();
        ApplyDevicePort(item.DevicePort);
        UpdateCommandPreview();
    }

    private bool SelectFormat(string fullName, string? preferredVendor = null)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var entry = _allFormats.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(preferredVendor)
            && x.Vendor == preferredVendor
            && x.Formats.Any(f => f.FullName == fullName));

        if (entry.Formats == null)
            entry = _allFormats.FirstOrDefault(x => x.Formats.Any(f => f.FullName == fullName));
        if (entry.Formats == null) return false;

        if (CboVendor.SelectedItem?.ToString() != entry.Vendor)
            CboVendor.SelectedItem = entry.Vendor;

        var fmt = entry.Formats.FirstOrDefault(f => f.FullName == fullName);
        if (fmt == null) return false;
        CboFormat.SelectedItem = fmt;
        return true;
    }

    private string? GetSelectedDriveValue()
    {
        return CboDrive?.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString()
            : null;
    }

    private string GetSelectedDriveProfileValue()
    {
        return CboDriveProfile?.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "Auto"
            : "Auto";
    }

    private void RestoreDriveProfile()
    {
        if (CboDriveProfile == null) return;
        string saved = string.IsNullOrWhiteSpace(_settings.DriveProfile)
            ? "Auto"
            : _settings.DriveProfile;

        foreach (var entry in CboDriveProfile.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(entry.Tag?.ToString(), saved, StringComparison.Ordinal))
            {
                CboDriveProfile.SelectedItem = entry;
                ApplyDriveProfile(updateDriveSelection: false);
                return;
            }
        }

        CboDriveProfile.SelectedIndex = 0;
        ApplyDriveProfile(updateDriveSelection: false);
    }

    private void SaveDriveProfile()
    {
        _settings.DriveProfile = GetSelectedDriveProfileValue();
        SettingsManager.Save(_settings);
    }

    private void ApplyDriveProfile(bool updateDriveSelection)
    {
        string profile = GetSelectedDriveProfileValue();
        _detectedDriveFamily = profile switch
        {
            "pc35"  => "3.5",
            "pc525" => "5.25",
            _       => null,
        };

        if (updateDriveSelection)
        {
            string? drive = profile switch
            {
                "shugart0" => "0",
                "shugart1" => "1",
                "shugart2" => "2",
                "shugart3" => "3",
                _          => null,
            };
            if (drive != null)
                ApplyDriveSelection(drive);
        }

        UpdateDriveFace();
    }

    private string? GetDriveProfileFamily()
    {
        return GetSelectedDriveProfileValue() switch
        {
            "pc35"  => "3.5",
            "pc525" => "5.25",
            _       => null,
        };
    }

    private string GetSelectedDriveName()
    {
        return CboDrive?.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? "Auto"
            : "Auto";
    }

    private void ApplyDriveSelection(string? drive, string? driveName = null)
    {
        if (CboDrive == null) return;

        foreach (var entry in CboDrive.Items.OfType<ComboBoxItem>())
        {
            if (!string.IsNullOrWhiteSpace(driveName)
                && string.Equals(entry.Content?.ToString(), driveName, StringComparison.Ordinal))
            {
                CboDrive.SelectedItem = entry;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(drive))
        {
            foreach (var entry in CboDrive.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(entry.Tag?.ToString(), drive, StringComparison.Ordinal))
                {
                    CboDrive.SelectedItem = entry;
                    return;
                }
            }
        }

        string legacyName = drive switch
        {
            "A" => "PC cable A: / Drive A",
            "B" => "PC cable B: / Drive B",
            "0" => "PC cable A: / Drive 0",
            "1" => "PC cable B: / Drive 1",
            _   => "Auto"
        };
        foreach (var entry in CboDrive.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(entry.Content?.ToString(), legacyName, StringComparison.Ordinal))
            {
                CboDrive.SelectedItem = entry;
                return;
            }
        }

        CboDrive.SelectedIndex = 0;
    }

    private void SaveDriveSelection()
    {
        _settings.LastDrive = GetSelectedDriveValue();
        _settings.LastDriveName = GetSelectedDriveName();
        SettingsManager.Save(_settings);
    }

    private void ApplyDevicePort(string? devicePort)
    {
        if (CboDevice == null) return;
        string selection = string.IsNullOrWhiteSpace(devicePort) ? "Auto" : devicePort;
        if (!CboDevice.Items.Contains(selection))
            CboDevice.Items.Add(selection);
        CboDevice.SelectedItem = selection;
    }

    private void RefreshPresetList(string? selectedName = null)
    {
        if (CboPreset == null) return;
        _refreshingPresets = true;
        CboPreset.Items.Clear();
        CboPreset.Items.Add(new ComboBoxItem { Content = "Default" });
        foreach (var preset in _settings.RunPresets.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            CboPreset.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });

        CboPreset.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            foreach (var item in CboPreset.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is RunPreset preset
                    && string.Equals(preset.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                {
                    CboPreset.SelectedItem = item;
                    break;
                }
            }
        }
        _refreshingPresets = false;
        UpdatePresetButtons();
    }

    private void UpdatePresetButtons()
    {
        if (BtnDeletePreset != null)
            BtnDeletePreset.IsEnabled = CboPreset?.SelectedItem is ComboBoxItem { Tag: RunPreset };
    }

    private RunPreset BuildCurrentPreset(string name)
    {
        GwOptions options = BuildCurrentOptions();
        return new RunPreset
        {
            Name = name,
            Vendor = CboVendor.SelectedItem as string ?? "",
            Format = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "",
            StartCyl = options.StartCyl,
            EndCyl = options.EndCyl,
            Retries = options.Retries,
            Verify = options.Verify,
            AdaptiveRetry = options.AdaptiveRetry,
            Drive = options.Drive,
            DriveName = GetSelectedDriveName(),
            Revs = options.Revs
        };
    }

    private void ApplyPreset(RunPreset preset)
    {
        SelectFormat(preset.Format, preset.Vendor);
        TxtCylStart.Text = (preset.StartCyl ?? 0).ToString();
        TxtCylEnd.Text   = (preset.EndCyl ?? 79).ToString();
        TxtRetries.Text  = (preset.Retries > 0 ? preset.Retries : 3).ToString();
        ChkVerify.IsChecked = preset.Verify;
        ChkAdaptiveRetry.IsChecked = preset.AdaptiveRetry;
        ApplyDriveSelection(preset.Drive, preset.DriveName);
        if (TxtRevs != null)
            TxtRevs.Text = (preset.Revs ?? 1).ToString();
        UpdateCommandPreview();
    }

    private void ApplyDefaultPreset()
    {
        SelectFormat("ibm.1440", "IBM PC");
        TxtCylStart.Text = "0";
        TxtCylEnd.Text   = "79";
        TxtRetries.Text  = "3";
        ChkVerify.IsChecked = true;
        ChkAdaptiveRetry.IsChecked = true;
        ApplyDriveSelection(null);
        if (TxtRevs != null)
            TxtRevs.Text = "1";
        UpdateCommandPreview();
    }

    private void CboPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPresets) return;
        if (CboPreset.SelectedItem is ComboBoxItem { Tag: RunPreset preset })
            ApplyPreset(preset);
        else
            ApplyDefaultPreset();
        UpdatePresetButtons();
    }

    private void BtnSavePreset_Click(object sender, RoutedEventArgs e)
    {
        string initialName = CboPreset?.SelectedItem is ComboBoxItem { Tag: RunPreset preset }
            ? preset.Name
            : "Custom";
        string? name = PromptPresetName(initialName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var existing = _settings.RunPresets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var result = MessageBox.Show(
                string.Concat("Replace preset '", existing.Name, "'?"),
                "HamsterWeazle", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;
            _settings.RunPresets.Remove(existing);
        }

        _settings.RunPresets.Add(BuildCurrentPreset(name.Trim()));
        SettingsManager.Save(_settings);
        RefreshPresetList(name.Trim());
        AppendLog(string.Concat("[preset saved] ", name.Trim()));
    }

    private void BtnDeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (CboPreset?.SelectedItem is not ComboBoxItem { Tag: RunPreset preset }) return;
        var result = MessageBox.Show(
            string.Concat("Delete preset '", preset.Name, "'?"),
            "HamsterWeazle", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        _settings.RunPresets.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        SettingsManager.Save(_settings);
        RefreshPresetList();
        AppendLog(string.Concat("[preset deleted] ", preset.Name));
    }

    private string? PromptPresetName(string initialName)
    {
        var input = new TextBox
        {
            Text = initialName,
            Margin = new Thickness(0, 4, 0, 10),
            MinWidth = 260
        };

        var ok = new Button
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 74,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 3, 10, 3)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 74,
            Padding = new Thickness(10, 3, 10, 3)
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Preset name" });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Owner = this,
            Title = "Save Preset",
            Content = panel,
            Width = 330,
            Height = 150,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured. Open Settings to locate it."); return; }
        if (_currentOp is GwOperation.Read or GwOperation.Write)
        {
            if (CboFormat.SelectedItem is not DiskFormat)
            { AppendLog("[error] Please select a disk format."); return; }
            bool needsImagePath = _currentOp == GwOperation.Write || ChkCustomOutput?.IsChecked == true;
            if (needsImagePath && string.IsNullOrWhiteSpace(TxtFile.Text))
            { AppendLog("[error] Please specify an image file path."); return; }
        }
        string format    = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        bool   autoInbox = _currentOp == GwOperation.Read && ChkCustomOutput?.IsChecked != true;
        string tempPath  = Path.Combine(GetInboxDir(), "Temp_Disk.img");
        string filePath  = autoInbox ? tempPath : TxtFile.Text.Trim();
        GwOptions options = BuildCurrentOptions();
        string originalFilePath = filePath;
        string? tempWritePath = null;
        if (_currentOp == GwOperation.Write
            && FormatGuesser.TryCreateRawImageFromDiskCopy42(filePath, out tempWritePath, out string? dc42Format))
        {
            filePath = tempWritePath;
            if (!string.IsNullOrEmpty(dc42Format))
            {
                format = dc42Format;
                SelectFormat(dc42Format);
            }
        }
        string args      = _runner.BuildArguments(_currentOp, format, filePath, options);
        if (_currentOp == GwOperation.Write)
            PushToWriteQueue(originalFilePath, format, options);
        SetRunning(true);
        if (tempWritePath != null)
            AppendLog("[converted DiskCopy 4.2 image to raw sector image for write]");
        int exitCode = -1;
        try
        {
            exitCode = _currentOp == GwOperation.Write
                ? await RunWriteWithAdaptiveRetryAsync(format, filePath, options)
                : await RunLoggedAsync(args, "");
            if (_currentOp == GwOperation.Write)
                OnProcessDone(exitCode);
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            if (autoInbox)
            {
                if (File.Exists(tempPath) && new FileInfo(tempPath).Length > 0)
                {
                    string finalPath = GenerateInboxPath();
                    try { File.Move(tempPath, finalPath, overwrite: true); AppendLog(string.Concat("[saved as ", Path.GetFileName(finalPath), "]")); }
                    catch { }
                }
                else { try { File.Delete(tempPath); } catch { } }
                RefreshInbox();
            }
            if (tempWritePath != null)
                try { File.Delete(tempWritePath); } catch { }
            SetRunning(false);
        }
    }

    private async void BtnEraseVerify_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOp != GwOperation.Erase) return;
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured. Open Settings to locate it."); return; }

        string format = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        if (string.IsNullOrWhiteSpace(format))
        { AppendLog("[error] Please select a disk format before using erase with verify."); return; }

        GwOptions options = BuildCurrentOptions();
        string eraseArgs = _runner.BuildArguments(GwOperation.Erase, "", "", options);
        string verifyPath = Path.Combine(Path.GetTempPath(), string.Concat("HamsterWeazle_EraseVerify_", Guid.NewGuid().ToString("N"), ".img"));
        string verifyArgs = _runner.BuildArguments(GwOperation.Read, format, verifyPath, options);

        SetRunning(true);
        bool finished = false;
        try
        {
            int eraseExit = await RunLoggedAsync(eraseArgs, "", deferProcessDone: true);
            if (eraseExit != 0)
            {
                OnProcessDone(eraseExit);
                finished = true;
                return;
            }

            AppendLog(string.Concat("[verify] checking erased disk against ", format));
            DriveStatusText.Text = "Verifying erase...";
            _driveHasError = false;
            _driveErrorFlashes = false;
            _suppressExpectedEraseVerifyReadIssues = true;
            int verifyExit = await RunLoggedAsync(verifyArgs, "[verify] ", deferProcessDone: true);
            _suppressExpectedEraseVerifyReadIssues = false;

            if (verifyExit == 0)
            {
                _driveHasError = true;
                _driveErrorFlashes = false;
                AppendIssue(string.Concat("[verify failed] Disk still decodes as ", format, " after erase."));
                DriveStatusText.Text = "Erase verify failed";
                SetRunning(false);
                finished = true;
                return;
            }

            _driveHasError = false;
            _driveErrorFlashes = false;
            AppendLog("[verify complete] No readable formatted data found after erase.");
            DriveStatusText.Text = "Erase verified - ready";
            SetRunning(false);
            finished = true;
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            _suppressExpectedEraseVerifyReadIssues = false;
            try { if (File.Exists(verifyPath)) File.Delete(verifyPath); } catch { }
            if (!finished)
                SetRunning(false);
        }
    }

    private async void BtnDetectEraseFormat_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOp != GwOperation.Erase) return;
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured. Open Settings to locate it."); return; }

        SetRunning(true);
        _autoCts = new CancellationTokenSource();
        AppendLog("[erase detect] Probing IBM 1.44 MB and Mac 800 KB...");
        AppendLog("");

        try
        {
            var result = await DetectEraseFormatAsync(_autoCts.Token);
            if (result.Format == null)
            {
                AppendLog("[erase detect] No readable IBM 1.44 MB or Mac 800 KB format found. If the disk is already blank, choose the format manually for verify.");
                return;
            }

            SelectFormat(result.Format);
            TxtAutoDetect.Visibility = Visibility.Visible;
            TxtAutoDetect.Text = string.Concat("Auto-detected: ", result.Format);
            AppendLog(string.Concat("[erase detect] Best match: ", result.Name, " (", result.Format, ")"));
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            SetRunning(false);
        }
    }

    private async Task<(string? Format, string? Name)> DetectEraseFormatAsync(CancellationToken token)
    {
        var candidates = new[]
        {
            ("ibm.1440", "IBM PC 1.44 MB HD"),
            ("mac.800",  "Apple Mac 800 KB DD"),
        };
        string tmpProbe = Path.Combine(Path.GetTempPath(), string.Concat("HamsterWeazle_EraseDetect_", Guid.NewGuid().ToString("N"), ".img"));
        string deviceArg = string.IsNullOrEmpty(_settings.DevicePort) ? "" : string.Concat(" --device ", _settings.DevicePort);
        string? bestFmt = null;
        string? bestName = null;
        int bestScore = -1;

        try
        {
            foreach (var (fmt, name) in candidates)
            {
                string probeArgs = string.Concat("read --format ", fmt, " --tracks c=0-1 --retries 0", deviceArg, " \"", tmpProbe, "\"");
                var psi = new System.Diagnostics.ProcessStartInfo(_runner.GwPath!, probeArgs)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                try { await p.WaitForExitAsync(token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }

                string raw = (await stdoutTask) + (await stderrTask);
                int ok = 0;
                int bad = 0;
                foreach (var ln in raw.Replace("\r", "").Split('\n'))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(ln, @"\b(\d+)/(\d+)\b");
                    if (m.Success && int.TryParse(m.Groups[2].Value, out int den) && den >= 8)
                    {
                        int num = int.Parse(m.Groups[1].Value);
                        if (num * 2 >= den) ok++; else bad++;
                    }
                    else if (ln.Contains("No sector", StringComparison.OrdinalIgnoreCase) ||
                             ln.Contains("unrecoverable", StringComparison.OrdinalIgnoreCase) ||
                             ln.Contains("CRC error", StringComparison.OrdinalIgnoreCase))
                    {
                        bad++;
                    }
                }

                int score = ok - bad;
                AppendLog(string.Concat("  ", name.PadRight(22), " score:", score >= 0 ? "+" : "", score, "  (", ok, " OK, ", bad, " errors)"));
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFmt = fmt;
                    bestName = name;
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmpProbe)) File.Delete(tmpProbe); } catch { }
        }

        return bestScore >= 0 ? (bestFmt, bestName) : (null, null);
    }

    private async void QuickWrite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WriteQueueItem item) return;
        if (!item.FileExists)
        { AppendLog(string.Concat("[error] File not found: ", item.FilePath)); return; }
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }
        // Switch to Write tab so the restored settings and Cancel button are visible.
        _currentOp = GwOperation.Write;
        foreach (var t in new[] { TabRead, TabWrite, TabErase, TabTools, TabInfo })
            t.IsChecked = t == TabWrite;
        UpdateTabUI();
        ApplyWriteQueueItemToControls(item);
        GwOptions options = BuildCurrentOptions();
        string format = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? item.Format;
        string filePath = TxtFile.Text.Trim();
        string originalFilePath = filePath;
        string? tempWritePath = null;
        if (FormatGuesser.TryCreateRawImageFromDiskCopy42(filePath, out tempWritePath, out string? dc42Format))
        {
            filePath = tempWritePath;
            if (!string.IsNullOrEmpty(dc42Format))
            {
                format = dc42Format;
                SelectFormat(dc42Format);
            }
        }
        PushToWriteQueue(originalFilePath, format, options);
        SetRunning(true);
        if (tempWritePath != null)
            AppendLog("[converted DiskCopy 4.2 image to raw sector image for write]");
        try
        {
            int exitCode = await RunWriteWithAdaptiveRetryAsync(format, filePath, options, "[quick write] ");
            OnProcessDone(exitCode);
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            if (tempWritePath != null)
                try { File.Delete(tempWritePath); } catch { }
            SetRunning(false);
        }
    }

    private async Task<int> RunLoggedAsync(string args, string prefix, bool deferProcessDone = false)
    {
        AppendLog(string.Concat(prefix, "$ gw.exe ", args));
        AppendLog("");
        bool previousDefer = _deferProcessDone;
        _deferProcessDone = deferProcessDone;
        try
        {
            return await _runner.RunAsync(args);
        }
        finally
        {
            _deferProcessDone = previousDefer;
        }
    }

    private async Task<int> RunWriteWithAdaptiveRetryAsync(
        string format,
        string filePath,
        GwOptions options,
        string prefix = "")
    {
        GwOptions current = options;
        int selectedStart = options.StartCyl ?? 0;
        int selectedEnd = options.EndCyl ?? 79;
        int exitCode = -1;

        for (int pass = 0; pass <= MaxAdaptiveWriteResumePasses; pass++)
        {
            _lastFailedVerifyCyl = null;
            string args = _runner.BuildArguments(GwOperation.Write, format, filePath, current);
            exitCode = await RunLoggedAsync(args, pass == 0 ? prefix : "[adaptive retry] ", deferProcessDone: true);

            if (exitCode == 0 || !current.Verify || !current.AdaptiveRetry)
                return exitCode;

            if (!_lastFailedVerifyCyl.HasValue || pass == MaxAdaptiveWriteResumePasses)
                return exitCode;

            int resumeCyl = Math.Max(selectedStart, _lastFailedVerifyCyl.Value);
            if (resumeCyl > selectedEnd)
                return exitCode;

            int boostedRetries = Math.Max(0, current.Retries) + 2;
            AppendLog(string.Concat(
                "[adaptive retry] restarting at track ",
                resumeCyl,
                " with retries ",
                boostedRetries));
            AppendLog("");

            current = current with
            {
                StartCyl = resumeCyl,
                EndCyl = selectedEnd,
                Retries = boostedRetries
            };
            _driveHasError = false;
            _driveErrorFlashes = false;
        }

        return exitCode;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _autoCts?.Cancel();
        _runner.Cancel();
        AppendLog("[cancelling...]");
    }

    private void SetRunning(bool running)
    {
        void Apply()
        {
            BtnRun.IsEnabled       = !running;
            BtnEraseVerify.IsEnabled = !running;
            BtnDetectEraseFormat.IsEnabled = !running;
            BtnCancel.IsEnabled    = running;
            ProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _driveIsRunning = running;
            if (running)
            {
                _driveHasError = false;
                _driveErrorFlashes = false;
                _driveLedOn = true;
                SetTrackDisplay(null);
                ErrorConsolePanel.Visibility = Visibility.Collapsed;
                TxtLog.Clear();
                DriveStatusText.Text = GetOperationStatusText();
                _driveLedTimer.Start();
            }
            else
            {
                if (_driveHasError && _driveErrorFlashes)
                    _driveLedTimer.Start();
                else
                    _driveLedTimer.Stop();
                _driveLedOn = _driveHasError;
            }
            UpdateDriveFace();
            UpdateDriveLed();
        }

        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.InvokeAsync(Apply);
    }

    private void OnProcessDone(int code)
    {
        if (code != 0)
        {
            _driveHasError = true;
            _driveErrorFlashes = _currentOp is GwOperation.Read or GwOperation.Write or GwOperation.Erase;
            AppendIssue(string.Concat("[exit code ", code, "]"));
            DriveStatusText.Text = "Stopped with an error";
        }
        else if (!_driveHasError)
        {
            DriveStatusText.Text = GetOperationCompleteStatusText();
        }
        SetRunning(false);
        if (_currentOp == GwOperation.Read) RefreshInbox();
    }

    private void AppendLog(string line)
    {
        if (_suppressExpectedEraseVerifyReadIssues)
        {
            UpdateSuppressedEraseVerifyStatus(line);
            if (IsIssueLine(line) && !IsExpectedEraseVerifyReadIssue(line))
                AppendIssue(line);
            return;
        }
        UpdateDriveStatusFromOutput(line);
        bool isIssue = IsIssueLine(line);
        if (isIssue || ShouldShowCommandOutput())
            AppendIssue(line);
    }

    private bool ShouldShowCommandOutput() =>
        _currentOp is GwOperation.Info or GwOperation.Tools;

    private void UpdateSuppressedEraseVerifyStatus(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        TryUpdateTrackDisplay(line);
    }

    private void AppendIssue(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        ErrorConsolePanel.Visibility = Visibility.Visible;
        TxtLog.AppendText(line + "\r\n");
        LogScroll.ScrollToBottom();
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
        ErrorConsolePanel.Visibility = Visibility.Collapsed;
        if (!_driveIsRunning)
        {
            _driveHasError = false;
            _driveErrorFlashes = false;
            _driveLedTimer.Stop();
            DriveStatusText.Text = "Ready";
            UpdateDriveLed();
        }
    }

    private void UpdateDriveStatusFromOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        RecordFailedVerifyTrack(line);
        bool trackUpdated = TryUpdateTrackDisplay(line);

        if (IsErrorLine(line))
        {
            _driveHasError = true;
            if (_currentOp is GwOperation.Read or GwOperation.Write or GwOperation.Erase)
            {
                _driveErrorFlashes = true;
                _driveLedTimer.Start();
            }
            DriveStatusText.Text = CleanStatusLine(line);
            UpdateDriveLed();
            return;
        }

        string lower = line.ToLowerInvariant();
        if (_driveHasError && _driveErrorFlashes && IsRecoveringLine(lower))
        {
            _driveHasError = false;
            _driveErrorFlashes = false;
            UpdateDriveLed();
        }
        if (lower.Contains("probing"))
            DriveStatusText.Text = "Probing disk format...";
        else if (lower.Contains("best match"))
            DriveStatusText.Text = CleanStatusLine(line);
        else if (lower.Contains("saved as"))
            DriveStatusText.Text = CleanStatusLine(line);
        else if (trackUpdated)
            return;
        else if ((lower.Contains("track") || lower.Contains("cyl")) && lower.Contains("verified"))
            DriveStatusText.Text = CleanStatusLine(line);
        else if (lower.Contains("rpm"))
            DriveStatusText.Text = CleanStatusLine(line);
    }

    private bool TryUpdateTrackDisplay(string line)
    {
        var match = GreaseweazleTrackNumberRegex.Match(line);
        if (!match.Success) return false;

        string value = match.Groups["compact"].Success
            ? match.Groups["compact"].Value
            : match.Groups["word"].Value;
        if (!int.TryParse(value, out int track)) return false;

        SetTrackDisplay(track);
        return true;
    }

    private void SetTrackDisplay(int? track)
    {
        if (TrackNumberText == null) return;
        TrackNumberText.Text = track.HasValue ? track.Value.ToString("00") : "--";
    }

    private void RecordFailedVerifyTrack(string line)
    {
        var match = FailedVerifyTrackRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int cyl))
            _lastFailedVerifyCyl = cyl;
    }

    private static bool IsIssueLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string lower = line.ToLowerInvariant();
        return lower.Contains("[error]")
            || lower.Contains("[warn]")
            || lower.Contains("[hint]")
            || lower.Contains("[cancelled]")
            || lower.Contains("failure")
            || lower.Contains("failed")
            || lower.Contains("giving up")
            || lower.Contains("sectors missing")
            || lower.Contains("missing sectors")
            || lower.Contains("not found")
            || lower.Contains("could not")
            || ContainsErrorWord(lower);
    }

    private static bool IsExpectedEraseVerifyReadIssue(string line)
    {
        string lower = line.ToLowerInvariant();
        return lower.Contains("no sector")
            || lower.Contains("no sync")
            || lower.Contains("sectors missing")
            || lower.Contains("missing sectors")
            || lower.Contains("unformatted")
            || lower.Contains("unrecoverable")
            || lower.Contains("crc error")
            || lower.Contains("failed")
            || lower.Contains("failure")
            || lower.Contains("giving up");
    }

    private static bool IsErrorLine(string line)
    {
        string lower = line.ToLowerInvariant();
        return lower.Contains("[error]")
            || lower.Contains("[cancelled]")
            || IsRetryFailureLine(lower)
            || lower.Contains("failed")
            || lower.Contains("giving up")
            || lower.Contains("sectors missing")
            || lower.Contains("missing sectors")
            || lower.Contains("not found")
            || lower.Contains("could not")
            || ContainsErrorWord(lower);
    }

    private static bool ContainsErrorWord(string lower) =>
        lower.Contains("error:")
        || lower.Contains(" error ")
        || lower.Contains(" error.");

    private static bool IsRetryFailureLine(string lower) =>
        lower.Contains("failure") && lower.Contains("retry");

    private static bool IsTrackStatusLine(string line) =>
        GreaseweazleTrackStatusRegex.IsMatch(line);

    private static bool IsRecoveringLine(string lower) =>
        !IsErrorLine(lower)
        && (lower.Contains("writing track")
            || lower.Contains("erasing track")
            || lower.Contains("from flux")
            || lower.Contains("macintosh gcr (")
            || lower.Contains("ibm mfm (")
            || lower.Contains("ibm fm (")
            || lower.Contains("amigados (")
            || lower.Contains("all tracks verified"));

    private static string CleanStatusLine(string line)
    {
        string text = line.Trim();
        while (text.StartsWith("[") && text.Contains(']'))
            text = text[(text.IndexOf(']') + 1)..].Trim();
        if (text.StartsWith("$")) text = "Running command";
        return string.IsNullOrWhiteSpace(text) ? "Working..." : text;
    }

    private string GetOperationStatusText() => _currentOp switch
    {
        GwOperation.Read  => "Reading disk...",
        GwOperation.Write => "Writing disk...",
        GwOperation.Erase => "Erasing disk...",
        GwOperation.Info  => "Checking device...",
        _                 => "Working..."
    };

    private string GetOperationCompleteStatusText() => _currentOp switch
    {
        GwOperation.Read  => "Read complete - ready",
        GwOperation.Write => ChkVerify?.IsChecked == true
            ? "Write complete - no errors - ready"
            : "Write complete - not verified - ready",
        GwOperation.Erase => "Erase complete - ready",
        GwOperation.Info  => "Device check complete - ready",
        _                 => "Complete - ready"
    };

    private void UpdateDriveFace()
    {
        if (Drive35Face == null) return;
        bool use35 = GetDriveFamilyForDisplay() == "3.5";
        Visibility show35 = use35 ? Visibility.Visible : Visibility.Collapsed;
        Visibility show525 = use35 ? Visibility.Collapsed : Visibility.Visible;

        Drive35Face.Visibility = show35;
        Drive35Bezel.Visibility = show35;
        Drive35Slot.Visibility = show35;
        Drive35Button.Visibility = show35;
        Drive35ButtonLine.Visibility = show35;
        Drive525Face.Visibility = show525;
        Drive525Slot.Visibility = show525;
        Drive525Latch.Visibility = show525;
        Drive525LatchLine.Visibility = show525;
        DriveFamilyLabel.Text = use35 ? "3.5\"" : "5.25\"";
        DriveWordLabel.Text = "DRIVE";
    }

    private string GetDriveFamilyForDisplay()
    {
        if (_detectedDriveFamily == "3.5" || _detectedDriveFamily == "5.25")
            return _detectedDriveFamily;

        string format = (CboFormat?.SelectedItem as DiskFormat)?.FullName ?? "";
        string label = CboFormat?.SelectedItem?.ToString() ?? "";
        if (format.Contains("1200", StringComparison.OrdinalIgnoreCase)
            || format.Contains("360", StringComparison.OrdinalIgnoreCase)
            || label.Contains("5.25", StringComparison.OrdinalIgnoreCase)
            || label.Contains("1541", StringComparison.OrdinalIgnoreCase)
            || label.Contains("1571", StringComparison.OrdinalIgnoreCase))
            return "5.25";

        return "3.5";
    }

    private void UpdateDriveLed()
    {
        if (DriveLed == null) return;

        if (_driveHasError)
        {
            DriveLed.Fill = new SolidColorBrush(Color.FromRgb(244, 71, 71));
            DriveLed.Opacity = _driveErrorFlashes
                ? (_driveLedOn ? 1.0 : 0.18)
                : 1.0;
            return;
        }

        DriveLed.Fill = new SolidColorBrush(Color.FromRgb(78, 201, 112));
        DriveLed.Opacity = _driveIsRunning
            ? (_driveLedOn ? 1.0 : 0.28)
            : 0.38;
    }

    private string GetInboxDir()
    {
        string dir = string.IsNullOrEmpty(_settings.InboxDir)
            ? Path.Combine(AppContext.BaseDirectory, "inbox")
            : _settings.InboxDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GenerateInboxPath()
    {
        string dir    = GetInboxDir();
        string fmt    = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "disk";
        string date   = DateTime.Now.ToString("yyyyMMdd");
        string prefix = string.Concat(fmt, "_");
        int next = 1;
        foreach (string f in Directory.GetFiles(dir, "*.img"))
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (stem.StartsWith(prefix) && int.TryParse(stem[prefix.Length..], out int n) && n >= next)
                next = n + 1;
        }
        return Path.Combine(dir, string.Concat(prefix, next, ".img"));
    }

    private void PushToWriteQueue(string filePath, string format, GwOptions options)
    {
        var items = _settings.WriteQueueItems;
        items.RemoveAll(i => i.FilePath == filePath && i.Format == format);
        items.Insert(0, new WriteQueueItem
        {
            FilePath = filePath,
            Format = format,
            Vendor = CboVendor.SelectedItem as string ?? "",
            StartCyl = options.StartCyl,
            EndCyl = options.EndCyl,
            Retries = options.Retries,
            Verify = options.Verify,
            AdaptiveRetry = options.AdaptiveRetry,
            Drive = options.Drive,
            DriveName = GetSelectedDriveName(),
            Revs = options.Revs,
            DevicePort = options.DevicePort ?? "",
            LastWritten = DateTime.Now
        });
        if (items.Count > 30) items.RemoveRange(30, items.Count - 30);
        SettingsManager.Save(_settings);
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
        foreach (var item in _settings.WriteQueueItems.Take(3))
            WriteQueuePanel.Children.Add(BuildQueueCard(item));
    }

    private FrameworkElement BuildQueueCard(WriteQueueItem item)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Win.Panel2");

        var sp = new StackPanel { Margin = new Thickness(9, 6, 9, 6) };

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fmtTxt = new TextBlock { Text = item.Format, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        fmtTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.Accent");
        fmtTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
        Grid.SetColumn(fmtTxt, 0);

        var runBtn = new Button { Content = "Run", Tag = item, Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
        runBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        runBtn.IsEnabled = item.FileExists;
        runBtn.Click += QuickWrite_Click;
        Grid.SetColumn(runBtn, 1);

        var dismissBtn = new Button { Content = "✕", Width = 22, Height = 22,
            Padding = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove from queue" };
        dismissBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        dismissBtn.Click += (_, _) =>
        {
            var r = MessageBox.Show(
                string.Concat("Remove '", item.FileName, "' from the Write Queue?"),
                "HamsterWeazle", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            _settings.WriteQueueItems.RemoveAll(i => i.FilePath == item.FilePath && i.Format == item.Format);
            SettingsManager.Save(_settings);
            RefreshWriteQueue();
        };
        Grid.SetColumn(dismissBtn, 2);

        row1.Children.Add(fmtTxt);
        row1.Children.Add(runBtn);
        row1.Children.Add(dismissBtn);

        var nameTxt = new TextBlock { Text = item.FileName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), FontWeight = FontWeights.SemiBold };
        nameTxt.SetResourceReference(TextBlock.ForegroundProperty, item.FileExists ? "Win.Text" : "Win.Error");
        nameTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMono");

        string parentDir = Path.GetFileName(Path.GetDirectoryName(item.FilePath) ?? "") ?? "";
        var pathTxt = new TextBlock { Text = parentDir.Length > 0 ? string.Concat("...", Path.DirectorySeparatorChar, parentDir, Path.DirectorySeparatorChar) : item.ShortPath, TextTrimming = TextTrimming.CharacterEllipsis };
        pathTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        pathTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");

        var dateTxt = new TextBlock { Text = string.Concat(item.DateLabel, "  |  Drive ", item.DriveLabel), FontSize = 11 };
        dateTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        dateTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");

        sp.Children.Add(row1);
        sp.Children.Add(nameTxt);
        sp.Children.Add(pathTxt);
        sp.Children.Add(dateTxt);

        var hxcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        var listBtn = new Button { Content = "List", Tag = new[] { item.FilePath, item.Format },
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand };
        ConfigureHxcListButton(listBtn, item.Format, item.FileExists);
        listBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "HxC", Tag = item.FilePath,
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
            .Where(f => !_settings.DismissedInboxFiles.Contains(f))
            .OrderByDescending(File.GetLastWriteTime)
            .Take(3)
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
        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Win.Panel2");

        var fi   = new FileInfo(filePath);
        string? fmtCode = GetFormatCodeForFile(filePath);
        double mb = fi.Length / (1024.0 * 1024.0);
        string size = mb >= 1.0
            ? string.Concat(mb.ToString("F1"), " MB")
            : string.Concat((fi.Length / 1024.0).ToString("F0"), " KB");

        var sp  = new StackPanel { Margin = new Thickness(9, 6, 9, 6) };

        // Name row — swaps to TextBox when renaming
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameTxt = new TextBlock { Text = fi.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        nameTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.Text");
        nameTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMono");
        Grid.SetColumn(nameTxt, 0);

        var sizeTxt = new TextBlock { Text = size, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sizeTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        sizeTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
        Grid.SetColumn(sizeTxt, 1);

        var dismissBtn = new Button { Content = "✕", Width = 22, Height = 22,
            Padding = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove from inbox" };
        dismissBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        dismissBtn.Click += (_, _) =>
        {
            var r = MessageBox.Show(
                string.Concat("Remove '", fi.Name, "' from the Inbox?\n\nYes = delete file from disk\nNo = remove from list only (file kept)"),
                "HamsterWeazle", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
                try { File.Delete(filePath); } catch { }
            else
            {
                if (!_settings.DismissedInboxFiles.Contains(filePath))
                    _settings.DismissedInboxFiles.Add(filePath);
                SettingsManager.Save(_settings);
            }
            RefreshInbox();
        };
        Grid.SetColumn(dismissBtn, 2);

        nameRow.Children.Add(nameTxt);
        nameRow.Children.Add(sizeTxt);
        nameRow.Children.Add(dismissBtn);
        sp.Children.Add(nameRow);

        // Rename TextBox (hidden until Rename clicked)
        string ext  = fi.Extension;
        string stem = Path.GetFileNameWithoutExtension(fi.Name);
        var renameTxt = new TextBox { Text = stem, Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(4, 2, 4, 2), Height = 22 };

        void CommitRename()
        {
            string newStem = renameTxt.Text.Trim();
            if (!string.IsNullOrEmpty(newStem) && newStem != stem)
            {
                string newPath = Path.Combine(fi.DirectoryName!, string.Concat(newStem, ext));
                try { File.Move(filePath, newPath); } catch { }
                RefreshInbox();
                return;
            }
            renameTxt.Visibility = Visibility.Collapsed;
            nameRow.Visibility   = Visibility.Visible;
        }

        renameTxt.KeyDown += (_, e2) =>
        {
            if (e2.Key == System.Windows.Input.Key.Enter)  CommitRename();
            if (e2.Key == System.Windows.Input.Key.Escape) { renameTxt.Visibility = Visibility.Collapsed; nameRow.Visibility = Visibility.Visible; }
        };
        renameTxt.LostFocus += (_, _) => CommitRename();
        sp.Children.Add(renameTxt);

        var fileDateTxt = new TextBlock { Text = fi.LastWriteTime.ToString("d MMM yyyy  HH:mm"), Margin = new Thickness(0, 1, 0, 0), FontSize = 11 };
        fileDateTxt.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
        fileDateTxt.SetResourceReference(TextBlock.FontFamilyProperty, "Win.FontMain");
        sp.Children.Add(fileDateTxt);

        // Button row
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        var writeBtn = new Button { Content = "Write", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand };
        writeBtn.SetResourceReference(Button.StyleProperty, "PrimaryBtn");
        writeBtn.Click += (_, _) =>
        {
            TxtFile.Text = filePath;
            TabWrite.IsChecked = true;
            OpTab_Click(TabWrite, new RoutedEventArgs());
        };

        var listBtn = new Button { Content = "List", Tag = new[] { filePath, fmtCode ?? "" },
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand };
        ConfigureHxcListButton(listBtn, fmtCode);
        listBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "HxC", Tag = filePath,
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = Cursors.Hand,
            IsEnabled = !string.IsNullOrEmpty(_settings.HxcPath) && File.Exists(_settings.HxcPath) };
        openBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        openBtn.Click += HxcOpen_Click;

        var renameBtn = new Button { Content = "Rename",
            Padding = new Thickness(6, 2, 6, 2), Height = 22, FontSize = 10, Cursor = Cursors.Hand };
        renameBtn.SetResourceReference(Button.StyleProperty, "GhostBtn");
        renameBtn.Click += (_, _) =>
        {
            renameTxt.Text = stem;
            nameRow.Visibility   = Visibility.Collapsed;
            renameTxt.Visibility = Visibility.Visible;
            renameTxt.SelectAll();
            renameTxt.Focus();
        };

        btnRow.Children.Add(writeBtn);
        btnRow.Children.Add(listBtn);
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(renameBtn);
        sp.Children.Add(btnRow);

        border.Child = sp;
        return border;
    }

    private CancellationTokenSource? _autoCts;

    private async void BtnAutoRead_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }

        bool   autoInbox = ChkCustomOutput?.IsChecked != true;
        string tempPath  = Path.Combine(GetInboxDir(), "Temp_Disk.img");
        string filePath  = autoInbox ? tempPath : (TxtFile?.Text.Trim() ?? "");
        if (!autoInbox && string.IsNullOrWhiteSpace(filePath))
        {
            string? pickedPath = PickImageSavePath();
            if (pickedPath == null) return;
            filePath = pickedPath;
            if (TxtFile != null) TxtFile.Text = filePath;
        }

        var candidates = new[]
        {
            ("ibm.1440",           "IBM PC 1.44 MB HD"),
            ("ibm.720",            "IBM PC 720 KB DD"),
            ("ibm.1200",           "IBM PC 1.2 MB HD 5.25\""),
            ("ibm.360",            "IBM PC 360 KB DD 5.25\""),
            ("amiga.amigados",     "Amiga 880 KB DD"),
            ("amiga.amigados_hd",  "Amiga 1.76 MB HD"),
            ("atarist.720",        "Atari ST 720 KB DS"),
            ("atarist.360",        "Atari ST 360 KB SS"),
            ("commodore.1541",     "Commodore 1541"),
            ("commodore.1571",     "Commodore 1571"),
            ("commodore.1581",     "Commodore 1581"),
            ("mac.800",            "Apple Mac 800 KB"),
            ("mac.400",            "Apple Mac 400 KB"),
            ("msx.2dd",            "MSX 720 KB DD"),
        };

        SetRunning(true);
        _autoCts = new CancellationTokenSource();
        AppendLog("[auto read] Probing format...");
        AppendLog("");

        string? bestFmt  = null;
        string? bestName = null;
        int     bestScore = -1;
        string  tmpProbe  = Path.Combine(Path.GetTempPath(), "hw_probe.img");
        string  deviceArg = string.IsNullOrEmpty(_settings.DevicePort) ? "" : string.Concat(" --device ", _settings.DevicePort);

        // ── Drive type detection ─────────────────────────────────────────────────
        IEnumerable<(string, string)> probeList = candidates;
        string? driveFamily = _detectedDriveFamily; // use cached result if available

        // Apply cached drive family filter without re-running Pin 34
        if (driveFamily == "3.5")
            probeList = candidates.Where(c => c.Item1 is not ("ibm.1200" or "ibm.360" or "commodore.1541" or "commodore.1571"));
        else if (driveFamily == "5.25")
            probeList = candidates.Where(c => c.Item1 is "ibm.1200" or "ibm.360" or "commodore.1541" or "commodore.1571");

        // Step 1: Pin 34 DISKCHANGE test — only runs once per session
        if (_settings.AutoDetectDriveType && _detectedDriveFamily == null)
        {
            try
            {
                async Task<string> RunAndRead(string args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(_runner.GwPath, args)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string raw = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                return raw;
            }

            await RunAndRead(string.Concat("seek 0", deviceArg));
            string before = await RunAndRead(string.Concat("pin get 34", deviceArg));
            await RunAndRead(string.Concat("seek 1 --motor-on", deviceArg));
            string after  = await RunAndRead(string.Concat("pin get 34", deviceArg));
            await RunAndRead(string.Concat("seek 0", deviceArg));

            bool wasLow = before.Contains("Low",  StringComparison.OrdinalIgnoreCase);
            bool isHigh = after.Contains("High", StringComparison.OrdinalIgnoreCase);

            if (wasLow && isHigh)
            {
                driveFamily = "3.5";
                _detectedDriveFamily = "3.5";
                UpdateDriveFace();
                AppendLog("[auto read] Pin 34: 3.5\" drive — skipping 5.25\" formats");
                probeList = candidates.Where(c => c.Item1 is not ("ibm.1200" or "ibm.360" or "commodore.1541" or "commodore.1571"));
                AppendLog("");
            }
            else if (wasLow && !isHigh)
            {
                driveFamily = "5.25";
                _detectedDriveFamily = "5.25";
                UpdateDriveFace();
                AppendLog("[auto read] Pin 34: 5.25\" drive — skipping 3.5\" formats");
                probeList = candidates.Where(c => c.Item1 is "ibm.1200" or "ibm.360" or "commodore.1541" or "commodore.1571");
                AppendLog("");
            }
            }
            catch { }
        }

        // Step 2: RPM detection — further narrows 5.25" (360 vs 300 rpm), or removes ibm.1200 for unknown drives
        // Skipped entirely if Pin 34 already confirmed 3.5"
        if (driveFamily != "3.5")
        {
            try
            {
                var rpmPsi = new System.Diagnostics.ProcessStartInfo(_runner.GwPath,
                    string.Concat("rpm", deviceArg))
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var rpmP = System.Diagnostics.Process.Start(rpmPsi)!;
                string rpmRaw = await rpmP.StandardOutput.ReadToEndAsync() + await rpmP.StandardError.ReadToEndAsync();
                await rpmP.WaitForExitAsync();
                var rpmMatch = System.Text.RegularExpressions.Regex.Match(rpmRaw, @"(\d{3}(?:\.\d+)?)");
                if (rpmMatch.Success && double.TryParse(rpmMatch.Groups[1].Value, out double detectedRpm)
                    && detectedRpm is >= 250 and <= 400)
                {
                    if (detectedRpm is >= 335 and <= 395)
                    {
                        AppendLog(string.Concat("[auto read] Drive RPM: ~", (int)detectedRpm, " — HD 5.25\" drive, testing 5.25\" formats only"));
                        probeList = probeList.Where(c => c.Item1 is "ibm.1200" or "ibm.360");
                    }
                    else if (detectedRpm is >= 265 and < 335)
                    {
                        AppendLog(string.Concat("[auto read] Drive RPM: ~", (int)detectedRpm, " — skipping HD 5.25\" format (ibm.1200)"));
                        probeList = probeList.Where(c => c.Item1 != "ibm.1200");
                    }
                    AppendLog("");
                }
            }
            catch { }
        }

        foreach (var (fmt, name) in probeList)
        {
            string probeArgs = string.Concat("read --format ", fmt, " --tracks c=0-1 --retries 0", deviceArg, " \"", tmpProbe, "\"");

            var psi = new System.Diagnostics.ProcessStartInfo(_runner.GwPath, probeArgs)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };

            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(_autoCts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } break; }
            string raw = (await stdoutTask) + (await stderrTask);

            int ok = 0; int bad = 0;
            foreach (var ln in raw.Replace("\r", "").Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(ln, @"\b(\d+)/(\d+)\b");
                if (m.Success && int.TryParse(m.Groups[2].Value, out int den) && den >= 8)
                {
                    int num = int.Parse(m.Groups[1].Value);
                    if (num * 2 >= den) ok++; else bad++;
                }
                else if (ln.Contains("No sector",     StringComparison.OrdinalIgnoreCase) ||
                         ln.Contains("unrecoverable", StringComparison.OrdinalIgnoreCase) ||
                         ln.Contains("CRC error",     StringComparison.OrdinalIgnoreCase))
                    bad++;
            }
            int score = ok - bad;
            bool strong = ok > 0 && bad == 0;
            string indicator = strong ? "  STRONG MATCH" : "";
            AppendLog(string.Concat("  ", name.PadRight(26), " score:", score >= 0 ? "+" : "", score,
                "  (", ok, " OK, ", bad, " errors)", indicator));

            if (score > bestScore) { bestScore = score; bestFmt = fmt; bestName = name; }

            if (strong)
            {
                AppendLog("");
                AppendLog("[auto read] Strong match — skipping remaining candidates.");
                break;
            }
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
        int exitCode = -1;
        try   { exitCode = await _runner.RunAsync(fullArgs); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            if (autoInbox)
            {
                if (File.Exists(tempPath) && new FileInfo(tempPath).Length > 0)
                {
                    string finalPath = GenerateInboxPath();
                    try { File.Move(tempPath, finalPath, overwrite: true); AppendLog(string.Concat("[saved as ", Path.GetFileName(finalPath), "]")); }
                    catch { }
                }
                else { try { File.Delete(tempPath); } catch { } }
                RefreshInbox();
            }
            SetRunning(false);
        }
    }

    private void BtnOpenInbox_Click(object sender, RoutedEventArgs e)
    { Process.Start("explorer.exe", GetInboxDir()); }

    private async void HxcList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !TryGetHxcListTag(btn, out string filePath, out string? fmtCode)) return;
        if (string.IsNullOrEmpty(fmtCode))
            fmtCode = GetFormatCodeForFile(filePath);
        if (!IsHxcListSupported(fmtCode))
        {
            AppendLog("[hint] HxC List is disabled for Apple Mac 800 KB images; hxcfe cannot list that filesystem.");
            return;
        }

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

    private string? GetFormatCodeForFile(string filePath)
    {
        string? code = FormatGuesser.Guess(filePath);
        if (code != null) return code;
        return _settings.WriteQueueItems
            .FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            ?.Format;
    }

    private static bool IsHxcListSupported(string? fmtCode) =>
        !string.Equals(fmtCode, "mac.800", StringComparison.OrdinalIgnoreCase);

    private static void ConfigureHxcListButton(Button button, string? fmtCode, bool fileExists = true)
    {
        if (IsHxcListSupported(fmtCode))
        {
            button.IsEnabled = fileExists;
            return;
        }

        button.IsEnabled = false;
        button.ToolTip = "HxC List is not available for Apple Mac 800 KB images";
    }

    private static bool TryGetHxcListTag(Button btn, out string filePath, out string? fmtCode)
    {
        filePath = "";
        fmtCode = null;
        if (btn.Tag is string path)
        {
            filePath = path;
            return true;
        }
        if (btn.Tag is string[] tag && tag.Length > 0)
        {
            filePath = tag[0];
            fmtCode = tag.Length > 1 ? tag[1] : null;
            return !string.IsNullOrWhiteSpace(filePath);
        }
        return false;
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

    private static readonly Dictionary<string, string> WhatsNewNotes = new()
    {
        ["1.5.3"] =
            "Image format choices\n" +
            "  READ custom output now offers separate IMG, DSK, HFE, SCP, ADF, Apple II, Commodore, Atari, Acorn, and other supported image containers.\n\n" +
            "Smarter defaults\n" +
            "  Save As now prefers the appropriate extension for formats such as Amiga ADF, Apple II DO/PO, Commodore D64/D71/D81, Atari ST, and Acorn SSD/DSD.\n\n" +
            "Image discovery\n" +
            "  The Open picker now recognizes the complete image-suffix set reported by the bundled GreaseWeazle tools.",
        ["1.5.2"] =
            "Device tools\n" +
            "  Info and firmware update output now stays visible, including already-current and updated firmware results.\n\n" +
            "Drive profiles\n" +
            "  Advanced now has a compact hardware profile hint for Auto Read and drive display, including PC 3.5 inch, PC 5.25 inch, and Shugart DS0-DS3 choices.\n\n" +
            "Formats\n" +
            "  Added missing imported Data General, Luxor, and Xerox disk definitions.",
        ["1.5.1"] =
            "Advanced settings\n" +
            "  The Advanced drive selection is now remembered between launches, including Shugart DS0-DS3 choices.\n\n" +
            "Erase drive selection\n" +
            "  The Erase tab now shows Advanced settings so erase operations can target the same selected drive.",
        ["1.5.0"] =
            "Erase workflow\n" +
            "  The Erase tab now has format selection, IBM 1.44 MB / Mac 800 KB detection, and Erase With Verify.\n\n" +
            "Drive display\n" +
            "  Track progress now appears as a green DSEG numeric display beside the drive LED instead of replacing the status text.\n\n" +
            "Erase status\n" +
            "  Erase supports track/revolution options, clearer verify behavior, and mini-console errors for real erase problems.",
        ["1.4.8"] =
            "Mac write reliability\n" +
            "  Mac 800K writes can now adaptive-retry from the failed track with boosted retries, and recovered retry errors return the drive LED to green.\n\n" +
            "Mac image detection\n" +
            "  Raw 400K/800K Mac .img files with Mac boot or HFS signatures auto-detect as mac.400/mac.800, while larger Mac images preserve an already selected Mac format.\n\n" +
            "GreaseWeazle command fixes\n" +
            "  Write verify now follows GW defaults correctly, DiskCopy 4.2 images are converted for write, and PC cable A:/B: drive selection maps to GW's A/B drive syntax.\n\n" +
            "Launch and status polish\n" +
            "  Successful writes now report completion clearly, and startup is guarded against a missing windir environment variable.",
        ["1.4.6"] =
            "Drive selection update\n" +
            "  Advanced drive selection now uses a compact dropdown with Auto, PC cable A:/B:, and Shugart DS0-DS3 options.\n\n" +
            "Run presets\n" +
            "  Save tested Advanced settings as named presets and apply them quickly before a read or write.\n\n" +
            "Write Queue restore\n" +
            "  Queue runs now restore the saved vendor, image path, drive choice, and write options before starting.\n\n" +
            "Tools\n" +
            "  Floppy Drive Cleaner now follows the same drive selector.",
        ["1.4.5"] =
            "Drive selection update\n" +
            "  Advanced drive selection now uses a compact dropdown with Auto, PC cable A:/B:, and Shugart DS0-DS3 options.\n\n" +
            "Write Queue restore\n" +
            "  Queue runs now restore the saved vendor, image path, drive choice, and write options before starting.",
        ["1.4.4"] =
            "Updater handoff fix\n" +
            "  Windows self-updates now wait for the app to exit with a direct PowerShell handoff before replacing the exe.\n\n" +
            "Mean Hamster releases\n" +
            "  HamsterWeazle still checks meanhamster.com first for Windows app downloads and release notes.\n\n" +
            "Everything else\n" +
            "  The GreaseWeazle and HxC update flow stays the same.",
        ["1.4.3"] =
            "Bridge update\n" +
            "  This Windows release lives on meanhamster.com so you can verify the move away from GitHub releases.\n\n" +
            "Updater path\n" +
            "  Once installed, future HamsterWeazle app updates continue checking Mean Hamster first.\n\n" +
            "Everything else\n" +
            "  The app keeps the same GreaseWeazle and HxC updater flow as before.",
        ["1.4.2"] =
            "Website downloads\n" +
            "  HamsterWeazle now downloads and checks for Windows app updates from meanhamster.com.\n\n" +
            "Product page\n" +
            "  The app now points its product link to the Mean Hamster HamsterWeazle page instead of GitHub.\n\n" +
            "Release flow\n" +
            "  This prepares HamsterWeazle for private source control while keeping public downloads live.",
        ["1.4.1"] =
            "Inbox filenames\n" +
            "  Reads now save as format_N.img (e.g. ibm.1440_1.img) — " +
            "date and time shown below the filename in the inbox panel.\n\n" +
            "Smarter auto-read\n" +
            "  RPM detection skips incompatible formats: 360 RPM drives only " +
            "test 5.25\" HD formats. Commodore 1571 added. Strong-match early stop.\n\n" +
            "Reliable saves\n" +
            "  Reads go to a temp file first and are only renamed on success — " +
            "no more overwriting a good image on a re-read.\n\n" +
            "UI\n" +
            "  Inbox and write queue panes now equal height with scrollbars. " +
            "X buttons moved inline. Log output wraps to window width.",
    };

    private void BtnWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        string ver = UpdateChecker.CurrentAppVersion();
        TxtWhatsNewHeader.Text = string.Concat("WHAT'S NEW IN v", ver);
        TxtWhatsNew.Text = WhatsNewNotes.TryGetValue(ver, out string? notes)
            ? notes : "No release notes for this version.";
        WhatsNewPopup.PlacementTarget = BtnWhatsNew;
        WhatsNewPopup.IsOpen = true;
        _settings.SeenWhatsNewVersion = ver;
        SettingsManager.Save(_settings);
        WhatsNewDot.Visibility = Visibility.Collapsed;
    }

    private async void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog { Owner = this };
        dlg.ShowDialog();
        _settings = SettingsManager.Load();
        RefreshPresetList();
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
        string? drive = GetSelectedDriveValue();
        string driveArg = string.IsNullOrWhiteSpace(drive) ? "" : string.Concat(" --drive ", drive);
        SetRunning(true);
        AppendLog(string.Concat("$ gw.exe clean", driveArg));
        AppendLog("Insert cleaning disk now and ensure drive motor is running.");
        AppendLog("");
        try   { await _runner.RunAsync(string.Concat("clean", driveArg)); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    private async void BtnResetGw_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw.exe not configured."); return; }
        AppendLog("$ gw.exe reset");
        AppendLog("");
        SetRunning(true);
        try   { await _runner.RunAsync("reset"); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex)               { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            ApplyDriveProfile(updateDriveSelection: false);
            UpdateDriveFace();
            SetRunning(false);
        }
    }

    private void PopulateDevicePorts()
    {
        CboDevice.Items.Clear();
        CboDevice.Items.Add("Auto");
        try
        {
            foreach (string port in System.IO.Ports.SerialPort.GetPortNames().OrderBy(p => p))
                CboDevice.Items.Add(port);
        }
        catch { }
        string saved = _settings.DevicePort ?? "";
        CboDevice.SelectedItem = string.IsNullOrEmpty(saved) ? "Auto"
            : (CboDevice.Items.Contains(saved) ? saved : "Auto");
    }

    private void CboDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboDevice.SelectedItem is not string sel) return;
        _settings.DevicePort = sel == "Auto" ? "" : sel;
        UpdateCommandPreview();
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
        if (_currentOp == GwOperation.Read)
            TabRead.IsChecked = true;
    }

    private void RestoreWriteOptions()
    {
        TxtRetries.Text = (_settings.Retries > 0 ? _settings.Retries : 3).ToString();
        ChkVerify.IsChecked = _settings.VerifyAfterWrite;
        ChkAdaptiveRetry.IsChecked = _settings.AdaptiveWriteRetry;
    }

    private void RestoreDriveSelection()
    {
        ApplyDriveSelection(_settings.LastDrive, _settings.LastDriveName);
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
        int.TryParse(TxtRetries?.Text, out int retries);
        _settings.Retries = retries > 0 ? retries : 3;
        _settings.VerifyAfterWrite = ChkVerify?.IsChecked == true;
        _settings.AdaptiveWriteRetry = ChkAdaptiveRetry?.IsChecked != false;
        _settings.DriveProfile = GetSelectedDriveProfileValue();
        _settings.LastDrive = GetSelectedDriveValue();
        _settings.LastDriveName = GetSelectedDriveName();
        if (CboVendor.SelectedItem is string v) _settings.LastVendor = v;
        SettingsManager.Save(_settings);
    }

    private string? _pendingGwZipUrl;
    private string? _pendingAppExeUrl;
    private string? _pendingAppPageUrl;

    private async Task CheckForUpdatesAsync(string gwCurrentVer)
    {
        try
        {
            var appRelease = await UpdateChecker.GetLatestAppReleaseAsync();
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
                _pendingAppPageUrl = appRelease.PageUrl;
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

            if (!string.IsNullOrEmpty(_pendingAppPageUrl))
            {
                AppendLog("[update] Opening Mean Hamster download page...");
                Process.Start(new ProcessStartInfo(_pendingAppPageUrl) { UseShellExecute = true });
                UpdateBanner.Visibility = Visibility.Collapsed;
                TxtUpdateMsg.Text = "";
                BtnDoUpdate.IsEnabled = true;
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
