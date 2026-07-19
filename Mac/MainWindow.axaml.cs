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
    private string? _detectedDriveFamily;
    private IReadOnlyList<(string Vendor, IReadOnlyList<DiskFormat> Formats)> _allFormats = [];
    private GwOperation _currentOp = GwOperation.Read;
    private readonly StringBuilder _logBuf = new();
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

    public MainWindow()
    {
        _settings = SettingsManager.Load();
        InitializeComponent();
        Width  = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        _driveLedTimer.Tick += (_, _) =>
        {
            _driveLedOn = !_driveLedOn;
            UpdateDriveLed();
        };
        _runner.OutputReceived    += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _runner.ProcessExited     += code =>
        {
            bool defer = _deferProcessDone;
            Dispatcher.UIThread.Post(() =>
            {
                if (!defer)
                    OnProcessDone(code);
            });
        };
        _hxcRunner.OutputReceived += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        Loaded += async (_, _) =>
        {
            TxtTitleVersion.Text = string.Concat(" v", UpdateChecker.CurrentAppVersion());
            WhatsNewDot.IsVisible = _settings.SeenWhatsNewVersion != UpdateChecker.CurrentAppVersion();
            PopulateDevicePorts();
            if (ChkAutoDetect != null) ChkAutoDetect.IsChecked = _settings.AutoDetectDriveType;
            LoadFormats(); RestoreWriteOptions(); RestoreDriveProfile(); RestoreLastOp(); await DetectGwAsync(); await DetectHxcAsync();
            RestoreLastFilePath(); UpdateTabUI(); UpdateCommandPreview(); RefreshSidebar(); UpdateDriveFace();
            _initialisingUi = false;
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
        PanelFormat.IsVisible = rw || erase;
        PanelErase.IsVisible  = erase;
        PanelTools.IsVisible  = tools;
        AdvExpander.IsVisible = rw;
        PanelRevs.IsVisible   = _currentOp == GwOperation.Read;
        BtnRun.IsVisible      = !tools;
        BtnEraseVerify.IsVisible = erase;
        BtnDetectEraseFormat.IsVisible = erase;
        BtnCancel.IsVisible   = !tools;
        BtnAutoRead.IsVisible = _currentOp == GwOperation.Read;
        BtnRun.Content = _currentOp switch
        {
            GwOperation.Read  => "  READ  ",
            GwOperation.Write => "  WRITE  ",
            GwOperation.Erase => "  ERASE  ",
            _                 => "  RUN  ",
        };
        BtnClearLog.Content = ShouldShowCommandOutput() ? "Clear Output" : "Clear Issues";
        LblFile.Content = _currentOp == GwOperation.Read ? "Save to:" : "Image file:";
        TxtAutoDetect.IsVisible = false;
        bool customOut = ChkCustomOutput?.IsChecked == true;
        PanelFile.IsVisible = _currentOp == GwOperation.Write ||
                              (_currentOp == GwOperation.Read && customOut);
        if (_currentOp == GwOperation.Read && !customOut)
            TxtFile.Text = GenerateInboxPath();
    }

    private void ChkCustomOutput_Changed(object? sender, RoutedEventArgs e) => UpdateTabUI();
    private void ChkAutoDetect_Changed(object? sender, RoutedEventArgs e)
    {
        _settings.AutoDetectDriveType = ChkAutoDetect?.IsChecked == true;
        SettingsManager.Save(_settings);
    }

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
        if (_currentOp == GwOperation.Read && ChkCustomOutput?.IsChecked != true)
            TxtFile.Text = GenerateInboxPath();
        UpdateCommandPreview();
        UpdateDriveFace();
    }

    // ─── File browse ─────────────────────────────────────────────────────────────
    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var imgTypes = BuildAllSupportedImageType();
        var allFiles = new FilePickerFileType("All files") { Patterns = ["*"] };

        if (_currentOp == GwOperation.Read)
        {
            string? filePath = await PickImageSavePathAsync();
            if (filePath != null) TxtFile.Text = filePath;
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
                if (_currentOp == GwOperation.Write)
                    TryAutoDetectFormat(files[0].Path.LocalPath);
            }
        }
        UpdateCommandPreview();
    }

    // ─── Options changed ─────────────────────────────────────────────────────────
    private void Options_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_initialisingUi && sender == CboDriveProfile)
        {
            ApplyDriveProfile();
            SaveDriveProfile();
        }
        UpdateCommandPreview();
    }

    private static FilePickerFileType BuildAllSupportedImageType() =>
        new("All supported disk images")
        {
            Patterns = DiskImageFileTypes.SupportedExtensions
                .Select(extension => string.Concat("*", extension))
                .ToArray(),
        };

    private static FilePickerFileType BuildImageType(DiskImageFileType type) =>
        new(string.Concat(type.Name, " (*", type.Extension, ")"))
        {
            Patterns = [type.Pattern],
        };

    private async Task<string?> PickImageSavePathAsync()
    {
        string format = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "";
        string preferredExtension = DiskImageFileTypes.PreferredExtension(format);
        string suggestedPath = TxtFile?.Text?.Trim() ?? "";
        string suggestedName = string.IsNullOrWhiteSpace(suggestedPath)
            ? "disk"
            : Path.GetFileNameWithoutExtension(suggestedPath);
        var choices = DiskImageFileTypes.SaveTypes
            .Select(BuildImageType)
            .Append(new FilePickerFileType("All files") { Patterns = ["*"] })
            .ToArray();
        int preferredIndex = DiskImageFileTypes.SaveTypeIndex(preferredExtension);
        if (preferredIndex > 0)
            (choices[0], choices[preferredIndex]) = (choices[preferredIndex], choices[0]);

        var opts = new FilePickerSaveOptions
        {
            Title = "Save disk image as...",
            SuggestedFileName = suggestedName,
            DefaultExtension = preferredExtension.TrimStart('.'),
            FileTypeChoices = choices,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(GetInboxDir()),
        };
        var result = await StorageProvider.SaveFilePickerAsync(opts);
        if (result == null) return null;
        string filePath = DiskImageFileTypes.NormalizeSavePath(result.Path.LocalPath);
        _settings.LastOutputDir = Path.GetDirectoryName(filePath) ?? "";
        return filePath;
    }

    private void TxtFile_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initialisingUi && _currentOp == GwOperation.Write)
            TryAutoDetectFormat(TxtFile.Text?.Trim() ?? "");
        UpdateCommandPreview();
    }

    private void Options_TextChanged(object? sender, TextChangedEventArgs e) => UpdateCommandPreview();

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
            CboVendor.SelectedItem = entry.Vendor;
        if (CboFormat.SelectedItem is not DiskFormat cur || cur.FullName != fullName)
        {
            CboFormat.SelectedItem  = fmt;
            TxtAutoDetect.IsVisible = true;
            TxtAutoDetect.Text      = string.Concat("Auto-detected: ", fullName);
        }
    }

    private bool SelectFormat(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        string[] parts = fullName.Split('.');
        if (parts.Length < 2) return false;
        string vendorKey = parts[0];
        var entry = _allFormats.FirstOrDefault(x =>
            x.Formats.Any(f => f.FullName.StartsWith(vendorKey + ".", StringComparison.OrdinalIgnoreCase)));
        if (entry == default) return false;
        var fmt = entry.Formats.FirstOrDefault(f => f.FullName == fullName);
        if (fmt == null) return false;
        if (CboVendor.SelectedItem?.ToString() != entry.Vendor)
            CboVendor.SelectedItem = entry.Vendor;
        CboFormat.SelectedItem = fmt;
        return true;
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
        string? drive  = RbDriveA?.IsChecked == true ? "A" : RbDriveB?.IsChecked == true ? "B" : null;
        string? device = string.IsNullOrEmpty(_settings.DevicePort) ? null : _settings.DevicePort;
        int.TryParse(TxtRevs?.Text, out int revs);
        return new GwOptions(StartCyl: range ? s : null, EndCyl: range ? e2 : null,
                             Retries: r, Verify: ChkVerify?.IsChecked == true,
                             AdaptiveRetry: ChkAdaptiveRetry?.IsChecked != false,
                             Drive: drive, Revs: revs > 1 ? revs : null,
                             DevicePort: device);
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

        for (int i = 0; i < CboDriveProfile.Items.Count; i++)
        {
            if (CboDriveProfile.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), saved, StringComparison.Ordinal))
            {
                CboDriveProfile.SelectedIndex = i;
                ApplyDriveProfile();
                return;
            }
        }

        CboDriveProfile.SelectedIndex = 0;
        ApplyDriveProfile();
    }

    private void SaveDriveProfile()
    {
        _settings.DriveProfile = GetSelectedDriveProfileValue();
        SettingsManager.Save(_settings);
    }

    private void ApplyDriveProfile()
    {
        _detectedDriveFamily = GetSelectedDriveProfileValue() switch
        {
            "pc35"  => "3.5",
            "pc525" => "5.25",
            _       => null,
        };
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

    // ─── Run / Cancel ─────────────────────────────────────────────────────────────
    private async void BtnRun_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw not configured."); return; }
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
        string filePath  = autoInbox ? tempPath : (TxtFile.Text?.Trim() ?? "");
        if (_currentOp == GwOperation.Read && !autoInbox)
        {
            filePath = DiskImageFileTypes.NormalizeSavePath(filePath);
            TxtFile.Text = filePath;
        }
        string originalFilePath = filePath;
        string? tempWritePath = null;
        string? tempMfmSourcePath = null;
        bool convertToMfm = _currentOp == GwOperation.Read && DiskImageFileTypes.IsMfmOutput(filePath);
        if (_currentOp == GwOperation.Write && DiskImageFileTypes.IsMfmOutput(filePath))
        {
            AppendLog("[error] MFM bitstream images are currently available as a Read output only.");
            return;
        }
        if (convertToMfm)
        {
            string? hintDir = !string.IsNullOrEmpty(_settings.HxcPath) ? Path.GetDirectoryName(_settings.HxcPath) : null;
            string? cli = _hxcRunner.GwPath ?? UpdateChecker.FindHxcCliExe(hintDir);
            if (string.IsNullOrEmpty(cli))
            {
                AppendLog("[error] MFM output requires HxCFloppyEmulator. Install it from Settings > Software Updates.");
                return;
            }
            _hxcRunner.GwPath = cli;
            tempMfmSourcePath = Path.Combine(Path.GetTempPath(), string.Concat("HamsterWeazle_", Guid.NewGuid().ToString("N"), ".scp"));
            filePath = tempMfmSourcePath;
        }
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
        GwOptions options = BuildCurrentOptions();
        if (convertToMfm || (_currentOp == GwOperation.Read && DiskImageFileTypes.IsKryoFluxRawOutput(filePath)))
            options = options with { Raw = true };
        string args      = _runner.BuildArguments(_currentOp, format, filePath, options);
        if (_currentOp == GwOperation.Write) PushToWriteQueue(originalFilePath, format, options);
        SetRunning(true);
        if (tempWritePath != null)
            AppendLog("[converted DiskCopy 4.2 image to raw sector image for write]");
        int exitCode = -1;
        try
        {
            exitCode = _currentOp == GwOperation.Write
                ? await RunWriteWithAdaptiveRetryAsync(format, filePath, options)
                : await RunLoggedAsync(args, "", deferProcessDone: convertToMfm);
            if (convertToMfm && exitCode == 0 && tempMfmSourcePath != null)
            {
                string hxcArgs = string.Concat("-finput:\"", tempMfmSourcePath,
                    "\" -conv:HXCMFM_IMG -foutput:\"", originalFilePath, "\"");
                AppendLog(string.Concat("$ hxcfe ", hxcArgs));
                AppendLog("");
                exitCode = await _hxcRunner.RunAsync(hxcArgs);
                if (exitCode == 0 && File.Exists(originalFilePath) && new FileInfo(originalFilePath).Length > 0)
                    AppendLog(string.Concat("[saved MFM bitstream image as ", Path.GetFileName(originalFilePath), "]"));
                else if (exitCode == 0)
                    exitCode = 1;
                OnProcessDone(exitCode);
            }
            else if (convertToMfm)
                OnProcessDone(exitCode);
            if (_currentOp == GwOperation.Write)
                OnProcessDone(exitCode);
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
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
            if (tempMfmSourcePath != null)
                try { File.Delete(tempMfmSourcePath); } catch { }
            SetRunning(false);
        }
    }

    private async void BtnEraseVerify_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentOp != GwOperation.Erase) return;
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw not configured."); return; }

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
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            _suppressExpectedEraseVerifyReadIssues = false;
            try { if (File.Exists(verifyPath)) File.Delete(verifyPath); } catch { }
            if (!finished)
                SetRunning(false);
        }
    }

    private async void BtnDetectEraseFormat_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentOp != GwOperation.Erase) return;
        if (string.IsNullOrEmpty(_runner.GwPath))
        { AppendLog("[error] gw not configured."); return; }

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
            TxtAutoDetect.IsVisible = true;
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
                var psi = new ProcessStartInfo(_runner.GwPath!, probeArgs)
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();
                try { await p.WaitForExitAsync(token); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }

                string raw = (await stdoutTask) + (await stderrTask);
                int ok = 0;
                int bad = 0;
                foreach (var ln in raw.Replace("\r", "").Split('\n'))
                {
                    var m = Regex.Match(ln, @"\b(\d+)/(\d+)\b");
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

    private async void QuickWrite_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not WriteQueueItem item) return;
        if (!item.FileExists) { AppendLog(string.Concat("[error] File not found: ", item.FilePath)); return; }
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        // Switch to Write tab so Cancel button is visible
        TabWrite.IsChecked = true;
        OpTab_Click(TabWrite, new RoutedEventArgs());
        string format = item.Format;
        string filePath = item.FilePath;
        string originalFilePath = filePath;
        string? tempWritePath = null;
        if (FormatGuesser.TryCreateRawImageFromDiskCopy42(filePath, out tempWritePath, out string? dc42Format))
        {
            filePath = tempWritePath;
            if (!string.IsNullOrEmpty(dc42Format))
                format = dc42Format;
        }
        GwOptions options = BuildQueueItemOptions(item);
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
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            if (tempWritePath != null)
                try { File.Delete(tempWritePath); } catch { }
            SetRunning(false);
        }
    }

    private async Task<int> RunLoggedAsync(string args, string prefix, bool deferProcessDone = false)
    {
        AppendLog(string.Concat(prefix, "$ gw ", args));
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

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    { _autoCts?.Cancel(); _runner.Cancel(); AppendLog("[cancelling...]"); }

    private void SetRunning(bool running)
    {
        void Apply()
        {
            BtnRun.IsEnabled = !running; BtnEraseVerify.IsEnabled = !running; BtnDetectEraseFormat.IsEnabled = !running; BtnCancel.IsEnabled = running; ProgressBar.IsVisible = running;
            _driveIsRunning = running;
            if (running)
            {
                _driveHasError = false;
                _driveErrorFlashes = false;
                _driveLedOn = true;
                SetTrackDisplay(null);
                ErrorConsolePanel.IsVisible = false;
                _logBuf.Clear();
                TxtLog.Text = "";
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

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
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
        ErrorConsolePanel.IsVisible = true;
        _logBuf.AppendLine(line);
        TxtLog.Text = _logBuf.ToString();
        Dispatcher.UIThread.Post(() =>
            LogScroll.Offset = new Vector(LogScroll.Offset.X, double.MaxValue),
            DispatcherPriority.Background);
    }

    private void BtnClearLog_Click(object? sender, RoutedEventArgs e)
    {
        _logBuf.Clear();
        TxtLog.Text = "";
        ErrorConsolePanel.IsVisible = false;
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

    private void RecordFailedVerifyTrack(string line)
    {
        var match = FailedVerifyTrackRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int cyl))
            _lastFailedVerifyCyl = cyl;
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

        Drive35Face.IsVisible = use35;
        Drive35Bezel.IsVisible = use35;
        Drive35Slot.IsVisible = use35;
        Drive35Button.IsVisible = use35;
        Drive35ButtonLine.IsVisible = use35;
        Drive525Face.IsVisible = !use35;
        Drive525Slot.IsVisible = !use35;
        Drive525Latch.IsVisible = !use35;
        Drive525LatchLine.IsVisible = !use35;
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
            ? RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(AppContext.BaseDirectory, "inbox")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HamsterWeazle")
            : _settings.InboxDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string GenerateInboxPath()
    {
        string dir    = GetInboxDir();
        string fmt    = (CboFormat.SelectedItem as DiskFormat)?.FullName ?? "disk";
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

    private void PushToWriteQueue(string filePath, string format, GwOptions? options = null)
    {
        var items = _settings.WriteQueueItems;
        items.RemoveAll(i => i.FilePath == filePath && i.Format == format);
        options ??= BuildCurrentOptions();
        items.Insert(0, new WriteQueueItem
        {
            FilePath = filePath,
            Format = format,
            StartCyl = options.StartCyl,
            EndCyl = options.EndCyl,
            Retries = options.Retries,
            Verify = options.Verify,
            AdaptiveRetry = options.AdaptiveRetry,
            Drive = options.Drive,
            Revs = options.Revs,
            DevicePort = options.DevicePort ?? "",
            LastWritten = DateTime.Now
        });
        if (items.Count > 30) items.RemoveRange(30, items.Count - 30);
        SettingsManager.Save(_settings);
        RefreshWriteQueue();
    }

    private static GwOptions BuildQueueItemOptions(WriteQueueItem item)
    {
        return new GwOptions(
            StartCyl: item.StartCyl,
            EndCyl: item.EndCyl,
            Retries: item.Retries > 0 ? item.Retries : 3,
            Verify: item.Verify,
            AdaptiveRetry: item.AdaptiveRetry,
            Drive: item.Drive,
            Revs: item.Revs,
            DevicePort: string.IsNullOrWhiteSpace(item.DevicePort) ? null : item.DevicePort);
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
        foreach (var item in _settings.WriteQueueItems.Take(3))
            WriteQueuePanel.Children.Add(BuildQueueCard(item));
    }

    private Control BuildQueueCard(WriteQueueItem item)
    {
        var border = new Border { Margin = new Thickness(0, 0, 0, 1) };
        if (Res<IBrush>("Win.Panel2") is { } bg) border.Background = bg;
        var sp  = new StackPanel { Margin = new Thickness(9, 6, 9, 6) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var fmtTxt = new TextBlock { Text = item.Format, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (Res<IBrush>("Win.Accent") is { } ac) fmtTxt.Foreground = ac;
        Grid.SetColumn(fmtTxt, 0);
        var runBtn = new Button { Content = "Run", Tag = item,
            Padding = new Thickness(8, 2, 8, 2),
            Cursor = new Cursor(StandardCursorType.Hand), IsEnabled = item.FileExists };
        runBtn.Classes.Add("primary"); runBtn.Click += QuickWrite_Click;
        Grid.SetColumn(runBtn, 1);
        var delQBtn = new Button { Content = "✕", Padding = new Thickness(0),
            Width = 22, Height = 22, FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand) };
        delQBtn.Classes.Add("ghost");
        delQBtn.Click += (_, _) => { _settings.WriteQueueItems.Remove(item); SettingsManager.Save(_settings); RefreshWriteQueue(); };
        Grid.SetColumn(delQBtn, 2);
        row.Children.Add(fmtTxt); row.Children.Add(runBtn); row.Children.Add(delQBtn);
        var nameTxt = new TextBlock { Text = item.FileName, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        if (Res<IBrush>(item.FileExists ? "Win.Text" : "Win.Error") is { } eb) nameTxt.Foreground = eb;
        string parentDir = Path.GetFileName(Path.GetDirectoryName(item.FilePath) ?? "") ?? "";
        var pathTxt = new TextBlock { Text = parentDir.Length > 0 ? parentDir + "/" : item.ShortPath,
            TextTrimming = TextTrimming.CharacterEllipsis };
        if (Res<IBrush>("Win.SubText") is { } s1) pathTxt.Foreground = s1;
        var dateTxt = new TextBlock { Text = item.DateLabel, FontSize = 11 };
        if (Res<IBrush>("Win.SubText") is { } s2) dateTxt.Foreground = s2;
        var hxcRow  = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var listBtn = new Button { Content = "List", Tag = new[] { item.FilePath, item.Format },
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand) };
        ConfigureHxcListButton(listBtn, item.Format, item.FileExists);
        listBtn.Classes.Add("primary"); listBtn.Click += HxcList_Click;
        var openBtn = new Button { Content = "HxC", Tag = item.FilePath,
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
        var files = Directory.GetFiles(dir).OrderByDescending(File.GetLastWriteTime).Take(3).ToList();
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
        var sp = new StackPanel { Margin = new Thickness(9, 6, 9, 6) };

        // ── top row: filename (or edit box) + size + delete ─────────
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        topRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var nameTxt = new TextBlock { Text = fi.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold };
        if (Res<IBrush>("Win.Text") is { } t) nameTxt.Foreground = t;
        nameTxt.Tapped += (_, _) => { TxtFile.Text = filePath; TabWrite.IsChecked = true; OpTab_Click(TabWrite, new RoutedEventArgs()); };
        Grid.SetColumn(nameTxt, 0);

        var nameEdit = new TextBox { IsVisible = false, Text = Path.GetFileNameWithoutExtension(fi.Name),
            Height = 22, FontSize = 11, Padding = new Thickness(4, 0) };
        Grid.SetColumn(nameEdit, 0);

        var sizeTxt = new TextBlock { Text = size, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        if (Res<IBrush>("Win.SubText") is { } s) sizeTxt.Foreground = s;
        Grid.SetColumn(sizeTxt, 1);

        var delBtn = new Button { Content = "✕", Padding = new Thickness(0),
            Width = 22, Height = 22, FontSize = 11, Margin = new Thickness(4, 0, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand) };
        delBtn.Classes.Add("ghost");
        delBtn.Click += (_, _) => { try { File.Delete(filePath); RefreshInbox(); } catch { } };
        Grid.SetColumn(delBtn, 2);
        topRow.Children.Add(nameTxt); topRow.Children.Add(nameEdit);
        topRow.Children.Add(sizeTxt); topRow.Children.Add(delBtn);

        // ── format label ────────────────────────────────────────────
        var fmtTxt = new TextBlock { Text = fmtLabel, IsVisible = !string.IsNullOrEmpty(fmtLabel),
            FontSize = 10, Margin = new Thickness(0, 1, 0, 0) };
        if (Res<IBrush>("Win.SubText") is { } sf) fmtTxt.Foreground = sf;

        var fileDateTxt = new TextBlock { Text = fi.LastWriteTime.ToString("d MMM yyyy  HH:mm"),
            FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
        if (Res<IBrush>("Win.SubText") is { } sd) fileDateTxt.Foreground = sd;

        // ── button row ───────────────────────────────────────────────
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

        var listBtn = new Button { Content = "List", Tag = new[] { filePath, fmtCode ?? "" },
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0),
            Height = 22, FontSize = 10, Cursor = new Cursor(StandardCursorType.Hand) };
        ConfigureHxcListButton(listBtn, fmtCode);
        listBtn.Classes.Add("primary"); listBtn.Click += HxcList_Click;

        var openBtn = new Button { Content = "HxC", Tag = filePath,
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
        sp.Children.Add(topRow); sp.Children.Add(fmtTxt); sp.Children.Add(fileDateTxt); sp.Children.Add(btnRow);
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
        GwOptions options = BuildCurrentOptions();
        PushToWriteQueue(filePath, fmtCode, options);
        SetRunning(true);
        try
        {
            int exitCode = await RunWriteWithAdaptiveRetryAsync(fmtCode, filePath, options, "[quick write] ");
            OnProcessDone(exitCode);
        }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally { SetRunning(false); }
    }

    // ─── Auto-read ────────────────────────────────────────────────────────────────
    private CancellationTokenSource? _autoCts;

    private async void BtnAutoRead_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        bool   autoInbox = ChkCustomOutput?.IsChecked != true;
        string tempPath  = Path.Combine(GetInboxDir(), "Temp_Disk.img");
        string filePath  = autoInbox ? tempPath : (TxtFile?.Text?.Trim() ?? "");
        if (!autoInbox && string.IsNullOrWhiteSpace(filePath))
        {
            string? pickedPath = await PickImageSavePathAsync();
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
        SetRunning(true); _autoCts = new CancellationTokenSource();
        AppendLog("[auto read] Probing format..."); AppendLog("");
        string? bestFmt = null; string? bestName = null; int bestScore = -1;
        string tmpProbe = Path.Combine(Path.GetTempPath(), "hw_probe.img");
        string deviceArg = string.IsNullOrEmpty(_settings.DevicePort) ? "" : $" --device {_settings.DevicePort}";

        // ── Drive type detection ─────────────────────────────────────────────────
        IEnumerable<(string, string)> probeList = candidates;
        string? driveFamily = _detectedDriveFamily;

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
                    var psi = new ProcessStartInfo(_runner.GwPath!, args)
                    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(psi)!;
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

        // Step 2: RPM detection — skipped if Pin 34 confirmed 3.5"
        if (driveFamily != "3.5")
        {
            try
            {
                var rpmPsi = new ProcessStartInfo(_runner.GwPath!, string.Concat("rpm", deviceArg))
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var rpmP = Process.Start(rpmPsi)!;
                string rpmRaw = await rpmP.StandardOutput.ReadToEndAsync() + await rpmP.StandardError.ReadToEndAsync();
                await rpmP.WaitForExitAsync();
                var rpmMatch = Regex.Match(rpmRaw, @"(\d{3}(?:\.\d+)?)");
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
            bool strong = ok > 0 && bad == 0;
            string indicator = strong ? "  STRONG MATCH" : "";
            AppendLog(string.Concat("  ", name.PadRight(26), " score:", score >= 0 ? "+" : "", score, "  (", ok, " OK, ", bad, " errors)", indicator));
            if (score > bestScore) { bestScore = score; bestFmt = fmt; bestName = name; }
            if (strong) { AppendLog(""); AppendLog("[auto read] Strong match — skipping remaining candidates."); break; }
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
        int exitCode = -1;
        try   { exitCode = await _runner.RunAsync(fullArgs); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
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

    private void BtnOpenInbox_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "explorer.exe" : "open", GetInboxDir()); }
        catch { }
    }

    private async void HxcList_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || !TryGetHxcListTag(btn, out string filePath, out string? fmtCode)) return;
        if (string.IsNullOrEmpty(fmtCode))
            fmtCode = GetFormatCodeForFile(filePath);
        if (!IsHxcListSupported(fmtCode))
        {
            AppendLog("[hint] HxC List is disabled for Apple Mac 800 KB images; hxcfe cannot list that filesystem.");
            return;
        }

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
        ToolTip.SetTip(button, "HxC List is not available for Apple Mac 800 KB images");
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

    private void HxcOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string filePath) return;
        string? gui = _settings.HxcPath ?? UpdateChecker.FindHxcGuiExe();
        if (string.IsNullOrEmpty(gui)) { AppendLog("[error] HxCFloppyEmulator not found."); return; }
        try { Process.Start(new ProcessStartInfo(gui, string.Concat("\"", filePath, "\"")) { UseShellExecute = true }); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
    }

    private static readonly Dictionary<string, string> WhatsNewNotes = new()
    {
        ["1.5.4"] =
            "Flux output choices\n" +
            "  READ custom output now offers MFM bitstream images and KryoFlux RAW track sets alongside the existing image containers.\n\n" +
            "Correct RAW naming\n" +
            "  KryoFlux captures automatically use the required per-track naming pattern such as disk00.0.raw and disk00.1.raw.\n\n" +
            "MFM conversion\n" +
            "  MFM output captures raw flux and converts it with HxC, with clear setup guidance when HxC is unavailable.",
        ["1.5.3"] =
            "Image format choices\n" +
            "  READ custom output now offers separate IMG, DSK, HFE, SCP, ADF, Apple II, Commodore, Atari, Acorn, and other supported image containers.\n\n" +
            "Smarter defaults\n" +
            "  Save As now prefers the appropriate extension for formats such as Amiga ADF, Apple II DO/PO, Commodore D64/D71/D81, Atari ST, and Acorn SSD/DSD.\n\n" +
            "Image discovery\n" +
            "  The Open picker now recognizes the complete image-suffix set reported by GreaseWeazle.",
        ["1.5.2"] =
            "Device tools\n" +
            "  Info and firmware update output now stays visible, including already-current and updated firmware results.\n\n" +
            "Drive profiles\n" +
            "  Advanced now has a compact hardware profile hint for Auto Read and drive display.\n\n" +
            "Formats\n" +
            "  Added missing imported Data General, Luxor, and Xerox disk definitions.",
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
            "  Write verify now follows GW defaults correctly, DiskCopy 4.2 images are converted for write, and drive A/B selection maps to GW's A/B drive syntax.\n\n" +
            "Status polish\n" +
            "  Successful writes now report completion clearly, including whether verify was enabled.",
        ["1.4.2"] =
            "Website downloads\n" +
            "  HamsterWeazle now checks meanhamster.com for app updates instead of public GitHub releases.\n\n" +
            "Product page\n" +
            "  The app's product link now opens the Mean Hamster HamsterWeazle page.\n\n" +
            "Release flow\n" +
            "  This keeps public downloads available while the source repo can move private.",
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

    private void BtnWhatsNew_Click(object? sender, RoutedEventArgs e)
    {
        string ver = UpdateChecker.CurrentAppVersion();
        string notes = WhatsNewNotes.TryGetValue(ver, out string? n)
            ? n : "No release notes for this version.";
        var dlg = new Window
        {
            Title    = string.Concat("What's New in v", ver),
            Width    = 400, Height = 360,
            CanResize = false,
        };
        var sp = new StackPanel { Margin = new Thickness(20) };
        var header = new TextBlock
        {
            Text = string.Concat("WHAT'S NEW IN v", ver),
            FontSize = 10, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        if (Res<IBrush>("Win.SubText") is { } c) header.Foreground = c;
        var body = new TextBlock
        {
            Text = notes, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
        };
        sp.Children.Add(header);
        sp.Children.Add(body);
        dlg.Content = new ScrollViewer { Content = sp };
        dlg.ShowDialog(this);
        _settings.SeenWhatsNewVersion = ver;
        SettingsManager.Save(_settings);
        WhatsNewDot.IsVisible = false;
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

    private async void BtnResetGw_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] gw not configured."); return; }
        AppendLog("$ gw reset");
        AppendLog("");
        SetRunning(true);
        try   { await _runner.RunAsync("reset"); }
        catch (OperationCanceledException) { AppendLog("[cancelled]"); }
        catch (Exception ex) { AppendLog(string.Concat("[error] ", ex.Message)); }
        finally
        {
            ApplyDriveProfile();
            UpdateDriveFace();
            SetRunning(false);
        }
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

    private void RestoreWriteOptions()
    {
        TxtRetries.Text = (_settings.Retries > 0 ? _settings.Retries : 3).ToString();
        ChkVerify.IsChecked = _settings.VerifyAfterWrite;
        ChkAdaptiveRetry.IsChecked = _settings.AdaptiveWriteRetry;
    }

    private void SaveSettings()
    {
        _settings.WindowWidth  = ClientSize.Width;
        _settings.WindowHeight = ClientSize.Height;
        _settings.LastOp       = _currentOp.ToString();
        _settings.LastFilePath = TxtFile?.Text?.Trim() ?? "";
        int.TryParse(TxtRetries?.Text, out int retries);
        _settings.Retries = retries > 0 ? retries : 3;
        _settings.VerifyAfterWrite = ChkVerify?.IsChecked == true;
        _settings.AdaptiveWriteRetry = ChkAdaptiveRetry?.IsChecked != false;
        _settings.DriveProfile = GetSelectedDriveProfileValue();
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
            var msg = new StringBuilder();
            if (appNewer)
            {
                msg.Append(string.Concat("HamsterWeazle ", appRelease!.TagName, " available"));
                _pendingAppExeUrl = appRelease.DownloadUrl;
                _pendingAppPageUrl = appRelease.PageUrl;
            }
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
                string newExe = Path.Combine(Path.GetTempPath(), "HamsterWeazle_update.zip");
                AppendLog("[update] Downloading HamsterWeazle update...");
                await UpdateChecker.DownloadAsync(_pendingAppExeUrl, newExe);
                UpdateChecker.LaunchSelfUpdateScript(newExe,
                    Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HamsterWeazle"));
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                return;
            }
            if (!string.IsNullOrEmpty(_pendingAppPageUrl))
            {
                AppendLog("[update] Opening Mean Hamster download page...");
                Process.Start(new ProcessStartInfo(_pendingAppPageUrl) { UseShellExecute = true });
                UpdateBanner.IsVisible = false;
                TxtUpdateMsg.Text = "";
                BtnDoUpdate.IsEnabled = true;
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
