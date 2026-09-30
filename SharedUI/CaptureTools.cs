using System.IO;
using HamsterWeazle.Services;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NativeControl = System.Windows.FrameworkElement;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NativeControl = Avalonia.Controls.Control;
#endif

namespace HamsterWeazle;

public partial class MainWindow
{
    private CaptureReport? _capture;
    private bool _mapDirty;
    private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void InitializeCaptureTools()
    {
        ChkTrackMap.IsChecked = _settings.ShowTrackMap;
        SetMapVisible(_settings.ShowTrackMap);
        _runner.ReportChanged += report => { _capture = report; _mapDirty = true; };
        _captureTimer.Tick += (_, _) => { if (_mapDirty && ChkTrackMap.IsChecked == true) RenderTrackMap(); };
        _captureTimer.Start();
#if WINDOWS
        Closed += (_, _) => _captureTimer.Stop();
#else
        Closed += (_, _) => _captureTimer.Stop();
#endif
    }

    private void TrackMap_Changed(object? sender, RoutedEventArgs e)
    {
        bool show = ChkTrackMap?.IsChecked == true;
        SetMapVisible(show);
        if (show) RenderTrackMap();
        if (!_initialisingUi) { _settings.ShowTrackMap = show; SettingsManager.Save(_settings); }
    }
    private void SetMapVisible(bool show)
    {
        if (TrackMapPanel == null) return;
#if WINDOWS
        TrackMapPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
#else
        TrackMapPanel.IsVisible = show;
#endif
    }
    private void RenderTrackMap()
    {
        if (TrackMapRows == null) return;
        _mapDirty = false;
        TrackMapRows.Children.Clear();
        var report = _capture;
        if (report == null) { TrackMapSummary.Text = "The next operation will appear here. Point to a cell for track details."; return; }
        var tracks = report.SnapshotTracks();
        if (report.Layout == null && tracks.Count == 0)
        {
            TrackMapSummary.Text = report.Format == "tandy.dmk"
                ? "DMK tools provide no decoded track map. Full output is saved in the capture report."
                : "No decoded track map is available. Full output is saved in the capture report.";
            return;
        }
        int columns = Math.Clamp(report.Layout?.Cylinders ?? (tracks.Count > 0 ? tracks.Max(t => t.Cylinder) + 1 : 80), 1, 256);
        int heads = Math.Clamp(report.Layout?.Heads ?? (tracks.Count > 0 ? tracks.Max(t => t.Head) + 1 : 2), 1, 2);
        var lookup = tracks.ToDictionary(t => (t.Cylinder, t.Head));
        for (int h = 0; h < heads; h++)
        {
            var row = new Grid();
#if WINDOWS
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
#else
            row.ColumnDefinitions.Add(new ColumnDefinition(42, GridUnitType.Pixel));
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
#endif
            var side = new TextBlock { Text = "Side " + h, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
#if WINDOWS
            side.SetResourceReference(TextBlock.ForegroundProperty, "Win.SubText");
#else
            if (Res<IBrush>("Win.SubText") is { } fg) side.Foreground = fg;
#endif
            row.Children.Add(side);
            var cells = new UniformGrid { Columns = columns, Rows = 1 };
            Grid.SetColumn(cells, 1);
            for (int c = 0; c < columns; c++)
            {
                lookup.TryGetValue((c, h), out var track);
                TrackState state = track?.State ?? TrackState.Pending;
                var (color, mark) = state switch
                {
                    TrackState.Read => ("#247D43", "·"), TrackState.Retried => ("#9A6821", "r"),
                    TrackState.Missing or TrackState.Failed => ("#AD3543", "×"), TrackState.HeaderMismatch => ("#7654A4", "?"),
                    TrackState.Written => ("#376888", "w"), TrackState.Verified => ("#247D43", "✓"),
                    TrackState.Reading => ("#9A8121", ""), _ => ("#41464B", "")
                };
#if WINDOWS
                var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
#else
                var brush = Brush.Parse(color);
#endif
                var cell = new Border { Background = brush, Margin = new Thickness(0.5, 1, 0.5, 1), Height = 15,
                    Child = new TextBlock { Text = mark, Foreground = Brushes.White, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center } };
                string tip = $"Track {c}.{h}: {state}" + (track == null ? "" : $" — {track.Found}/{track.Expected} sectors, {track.Attempts} attempts");
#if WINDOWS
                cell.ToolTip = tip; System.Windows.Automation.AutomationProperties.SetName(cell, tip);
#else
                ToolTip.SetTip(cell, tip); Avalonia.Automation.AutomationProperties.SetName(cell, tip);
#endif
                cells.Children.Add(cell);
            }
            row.Children.Add(cells); TrackMapRows.Children.Add(row);
        }
        TrackMapSummary.Text = report.Format + " · " + report.Summary();
    }

    private Task ShowImageBrowserAsync(string? path) => ImageToolsDialog.OpenAsync(this, path, RefreshSidebar);
    private void FinalizeConvertedCapture(int exitCode)
    {
        if (_runner.LastReport is not { } report) return;
        try
        {
            report.Feed("[image conversion exit code " + exitCode + "]");
            report.Finish(exitCode, report.Cancelled);
            CaptureStore.Save(report);
            _capture = report; _mapDirty = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppendLog("[warn] Conversion report could not be saved: " + ex.Message); }
    }
    private async void BtnImageTools_Click(object? sender, RoutedEventArgs e) => await ShowImageBrowserAsync(null);

    private async void BtnRescueRead_Click(object? sender, RoutedEventArgs e)
    {
        if (_driveIsRunning) return;
        string? image = await ImageToolsDialog.PickOpenAsync(this, "Choose a previous capture to recover", ["*.img", "*.ima", "*.adf", "*.st"]);
        if (image != null) await RescueImageAsync(image);
    }
    private async Task RescueImageAsync(string image)
    {
        if (_driveIsRunning) return;
        if (string.IsNullOrEmpty(_runner.GwPath)) { AppendLog("[error] GreaseWeazle is not configured."); return; }
        try
        {
            var report = CaptureStore.FindForImage(image);
            if (report?.Layout?.AcceptsContainer(image) != true) throw new InvalidOperationException("Choose a flat IBM PC, Amiga, Atari ST or MSX image with a matching HamsterWeazle read report.");
            if (!await ImageToolsDialog.ConfirmAsync(this, "Insert the SAME floppy used for " + Path.GetFileName(image) +
                ".\n\nRescue Read will read it again using " + report.Format + " and the currently selected drive/device. Both passes will be kept, and a separate combined image will be created.")) return;
            SelectFormat(report.Format);
            _currentOp = GwOperation.Read;
            TabRead.IsChecked = true; OpTab_Click(TabRead, new RoutedEventArgs());
            SetRunning(true); _deferProcessDone = true;
            var result = await RescueReader.ReadAsync(_runner, image, BuildCurrentOptions());
            _capture = CaptureStore.FindForImage(result.ImagePath); _mapDirty = true;
            _driveHasError = result.Remaining > 0 || result.Conflicts > 0;
            DriveStatusText.Text = $"Recovered {result.Recovered} sectors · {result.Remaining} remaining · {result.Conflicts} conflicts";
            if (result.Conflicts > 0) AppendIssue("[warn] Valid reads differed. Earlier valid data was kept; both source captures are available for review.");
            TxtFile.Text = result.ImagePath;
        }
        catch (OperationCanceledException) { DriveStatusText.Text = "Rescue cancelled — captures retained"; }
        catch (Exception ex) { AppendLog("[error] " + ex.Message); }
        finally { _deferProcessDone = false; SetRunning(false); RefreshInbox(); }
    }

    private async void BtnAnalyzeLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_driveIsRunning) return;
        string? path = await ImageToolsDialog.PickOpenAsync(this, "Analyze saved GreaseWeazle output", ["*.txt", "*.log"]);
        if (path == null) return;
        try
        {
            string format = (CboFormat.SelectedItem as Models.DiskFormat)?.FullName ?? "ibm.1440";
            string log = File.ReadAllText(path);
            var recorded = System.Text.RegularExpressions.Regex.Match(log, @"^Format[: ]+([^\s]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (recorded.Success) format = recorded.Groups[1].Value;
            string json = Path.ChangeExtension(path, ".json");
            _capture = log.StartsWith("HamsterWeazle capture report") && File.Exists(json)
                ? System.Text.Json.JsonSerializer.Deserialize<CaptureReport>(File.ReadAllText(json))
                : CaptureReport.Replay(log, format, System.Text.RegularExpressions.Regex.IsMatch(log, @"T\d+\.\d+: Writing Track") ? "write" : "read");
            if (_capture?.SchemaVersion != 1) throw new InvalidDataException("This capture report version is not supported.");
            _mapDirty = true;
            ChkTrackMap.IsChecked = true;
            RenderTrackMap();
            await ImageToolsDialog.ShowTextAsync(this, "Saved log analysis", _capture.ToText());
        }
        catch (Exception ex) { AppendLog("[error] " + ex.Message); }
    }
    private void AttachCaptureMenu(NativeControl card, string image)
    {
        var menu = new ContextMenu();
        var browse = new MenuItem { Header = "Browse DOS files..." };
        browse.Click += async (_, _) => { if (!_driveIsRunning) await ShowImageBrowserAsync(image); };
        var report = new MenuItem { Header = "View capture report..." };
        report.Click += async (_, _) =>
        {
            try
            {
                var saved = CaptureStore.FindForImage(image, readOnly: false);
                await ImageToolsDialog.ShowTextAsync(this, "Capture report", saved?.ToText() ?? "No matching capture report was found for this image.");
            }
            catch (Exception ex) { AppendLog("[error] " + ex.Message); }
        };
        var rescue = new MenuItem { Header = "Rescue Read..." };
        rescue.Click += async (_, _) => await RescueImageAsync(image);
#if WINDOWS
        menu.Items.Add(browse); menu.Items.Add(report); menu.Items.Add(rescue);
#else
        menu.ItemsSource = new[] { browse, report, rescue };
#endif
        card.ContextMenu = menu;
    }
}
