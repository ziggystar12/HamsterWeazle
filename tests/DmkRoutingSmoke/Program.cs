using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HamsterWeazle;
using HamsterWeazle.Models;
using HamsterWeazle.Services;

internal static class Program
{
    private const string CaptureVariable = "HAMSTERWEAZLE_DMK_SMOKE_CAPTURE";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        // Copies of this apphost act as recording tools. No hardware tool is invoked.
        string exe = Path.GetFileName(Environment.ProcessPath!);
        if (exe is "gw2dmk.exe" or "dmk2gw.exe" or "gw.exe")
        {
            string capture = Environment.GetEnvironmentVariable(CaptureVariable)
                ?? throw new InvalidOperationException("Test capture path is required.");
            File.WriteAllLines(capture, [exe, .. args]);
            if (Environment.GetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE") == "auto-cancel" && args[0] == "read")
            {
                int formatIndex = Array.IndexOf(args, "--format");
                if (formatIndex >= 0 && args[formatIndex + 1] == "ibm.1440")
                    foreach (int c in new[] { 0, 1 }) foreach (int h in new[] { 0, 1 }) Console.WriteLine($"T{c}.{h}: IBM MFM (9/18 sectors)");
                else Thread.Sleep(30000);
                return 0;
            }
            if (Environment.GetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE") == "early-failure")
            {
                Console.WriteLine("gw2dmk.exe version: test");
                Console.Error.WriteLine("gw2dmk.exe: Track 0 side 0 is unformatted.");
                return 1;
            }
            if (Environment.GetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE") == "bad-read")
            {
                Console.WriteLine("0 good tracks, 0 good sectors (0 FM + 0 MFM + 0 RX02)");
                Console.WriteLine("1 bad track, 1 unrecovered error, 0 retries");
                return 0; // The real tool can return zero for a capture with unreadable sectors.
            }
            int logIndex = Array.IndexOf(args, "-u");
            if (logIndex >= 0) File.WriteAllText(args[logIndex + 1], "Simulated DMK read log.");
            return 0;
        }

        try
        {
            RunChecks();
            Console.WriteLine("PASS: update controls/status, drive selection, preview/execution parity, cleaning, and DMK routing/diagnostics.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunChecks()
    {
        string work = Path.Combine(AppContext.BaseDirectory, "smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string capture = Path.Combine(work, "arguments.txt");
        Environment.SetEnvironmentVariable(CaptureVariable, capture);
        foreach (string tool in new[] { "gw2dmk.exe", "dmk2gw.exe", "gw.exe" })
            File.Copy(Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, tool), overwrite: true);

        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var window = new MainWindow(); // Remains hidden; Loaded detection never runs.
        RunUpdateChecks(window);
        RunCaptureUiChecks(window);
        RunWorkbenchChecks(window);
        var settings = Field<AppSettings>(window, "_settings");
        RunInboxChecks(window, work);
        settings.InboxDir = work;
        settings.DmkToolPath = AppContext.BaseDirectory;
        settings.DevicePort = "COM17";
        settings.LastDrive = "0"; // Preview/execution must use the current UI selection instead.
        var drive = Control<ComboBox>(window, "CboDrive");
        drive.SelectedItem = drive.Items.OfType<ComboBoxItem>().Single(x => (string?)x.Tag == "1");
        Control<CheckBox>(window, "ChkCustomOutput").IsChecked = true;
        Field<GwRunner>(window, "_runner").GwPath = Path.Combine(AppContext.BaseDirectory, "gw.exe");

        string file = Path.Combine(work, "Model II & John's disk (1).DMK");
        File.WriteAllText(file, "Test input; not a real disk image.");
        RunDriveChecks(window, capture, Path.ChangeExtension(file, ".img"));
        drive.SelectedItem = drive.Items.OfType<ComboBoxItem>().Single(x => (string?)x.Tag == "1");
        string log = Path.Combine(AppContext.BaseDirectory, "gw2dmk-last.log");
        string writeLog = Path.Combine(AppContext.BaseDirectory, "dmk2gw-last.log");
        string consoleLog = Path.Combine(AppContext.BaseDirectory, "gw2dmk-last-console.log");
        string readPreview = $"gw2dmk.exe -G \"COM17\" -B shugart -s 1 -v 22 -u \"{log}\" -d \"1\" \"{file}\"";
        string writePreview = $"dmk2gw.exe -G \"COM17\" -B shugart -v 11 -u \"{writeLog}\" -d \"1\" \"{file}\"";

        Select(window, GwOperation.Read, "tandy.dmk", file);
        Equal(readPreview, Control<TextBlock>(window, "TxtCmdPreview").Text, "read preview");
        RunButton(window);
        EqualLines(["gw2dmk.exe", "-G", "COM17", "-B", "shugart", "-s", "1", "-v", "22", "-u", log, "-d", "1", file], capture);
        Check(File.ReadAllText(log) == "Simulated DMK read log.", "read log destination");
        Check(File.ReadAllText(consoleLog).Contains("[exit code 0]"), "successful console log");
        Check(CaptureStore.FindForImage(file)?.Log.Contains("Simulated DMK read log.") == true, "fresh native DMK log follows the captured image");

        Select(window, GwOperation.Write, "ibm.1440", file);
        Equal(writePreview, Control<TextBlock>(window, "TxtCmdPreview").Text, "write extension routing");
        RunButton(window);
        EqualLines(["dmk2gw.exe", "-G", "COM17", "-B", "shugart", "-v", "11", "-u", writeLog, "-d", "1", file], capture);
        Check(Control<TextBlock>(window, "DriveStatusText").Text.Contains("not verified"), "DMK write completion is explicitly unverified");

        Select(window, GwOperation.Read, "tandy.dmk", Path.ChangeExtension(file, ".img"));
        string normalized = Path.ChangeExtension(file, ".dmk");
        Check(Control<TextBlock>(window, "TxtCmdPreview").Text.EndsWith($"\"{normalized}\""), "normalized preview path");
        RunButton(window);
        Equal(normalized, File.ReadAllLines(capture)[^1], "normalized process path");

        Select(window, GwOperation.Write, null, file);
        Equal(writePreview, Control<TextBlock>(window, "TxtCmdPreview").Text, "DMK without selected format");
        RunButton(window);
        Equal("dmk2gw.exe", File.ReadAllLines(capture)[0], "DMK extension-only process");

        foreach (var operation in new[] { GwOperation.Erase, GwOperation.Info })
        {
            Select(window, operation, "tandy.dmk", file);
            Check(Control<TextBlock>(window, "TxtCmdPreview").Text.StartsWith($"gw.exe {operation.ToString().ToLowerInvariant()} "), "non-DMK preview");
            RunButton(window);
            string[] recorded = File.ReadAllLines(capture);
            Equal("gw.exe", recorded[0], "non-DMK process");
            Equal(operation.ToString().ToLowerInvariant(), recorded[1], "non-DMK operation");
        }

        Select(window, GwOperation.Read, "ibm.1440", Path.ChangeExtension(file, ".img"));
        Check(Control<TextBlock>(window, "TxtCmdPreview").Text.StartsWith("gw.exe read "), "ordinary read preview");
        RunButton(window);
        Equal("gw.exe", File.ReadAllLines(capture)[0], "ordinary read process");

        Select(window, GwOperation.Read, "tandy.dmk", file);
        File.WriteAllText(Path.Combine(work, "tandy.dmk_1.dmk"), "Keep existing capture.");
        Equal(Path.Combine(work, "tandy.dmk_2.dmk"), (string)Invoke(window, "GenerateInboxPath")!, "numbered DMK inbox path");

        Select(window, GwOperation.Write, "raw.125", file);
        Field<GwRunner>(window, "_runner").GwPath = null;
        var queueItem = new WriteQueueItem
        {
            FilePath = file, Format = "raw.125", Vendor = "Raw Flux",
            Drive = "1", DevicePort = "COM17", Verify = true
        };
        Invoke(window, "QuickWrite_Click", new Button { Tag = queueItem }, new RoutedEventArgs());
        WaitForOperation(window);
        EqualLines(["dmk2gw.exe", "-G", "COM17", "-B", "shugart", "-v", "11", "-u", writeLog, "-d", "1", file], capture);
        Check(settings.WriteQueueItems[0].Format == "tandy.dmk" && !settings.WriteQueueItems[0].Verify, "DMK queue history");

        Select(window, GwOperation.Read, "tandy.dmk", file);
        File.Delete(log);
        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", "early-failure");
        RunButton(window, expectError: true);
        Check(!File.Exists(log), "simulated failure occurred before native logfile creation");
        string failureLog = File.ReadAllText(consoleLog);
        Check(failureLog.Contains("version: test") && failureLog.Contains("Track 0 side 0 is unformatted.")
            && failureLog.Contains("[exit code 1]"), "early stdout/stderr and exit code persist");
        Check(Control<TextBox>(window, "TxtLog").Text.Contains("Track 0 side 0 is unformatted."), "fatal diagnostic is visible");

        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", "bad-read");
        RunButton(window, expectError: true);
        Check(!Control<TextBlock>(window, "DriveStatusText").Text.Contains("Read complete"), "zero exit code cannot hide unrecovered read errors");
        Check(CaptureStore.FindForImage(file)?.Fatal == true, "DMK saved report retains unrecovered errors even with zero exit");
        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", null);
        Field<GwRunner>(window, "_runner").GwPath = Path.Combine(AppContext.BaseDirectory, "gw.exe");
        typeof(MainWindow).GetField("_detectedDriveFamily", PrivateInstance)!.SetValue(window, "3.5");
        Select(window, GwOperation.Read, "ibm.1440", Path.ChangeExtension(file, ".img"));
        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", "auto-cancel");
        Invoke(window, "BtnAutoRead_Click", Control<Button>(window, "BtnAutoRead"), new RoutedEventArgs());
        DateTime probeDeadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.ReadAllLines(capture).Contains("ibm.720"))
        {
            Check(DateTime.UtcNow < probeDeadline, "second Auto Read probe starts");
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); Thread.Sleep(10);
        }
        Invoke(window, "BtnCancel_Click", Control<Button>(window, "BtnCancel"), new RoutedEventArgs());
        WaitForOperation(window, expectError: true); // Cancellation uses the existing issue indicator.
        Check(Control<TextBlock>(window, "DriveStatusText").Text == "Auto Read cancelled" && File.ReadAllLines(capture).Contains("ibm.720"),
            "cancelling a later probe cannot launch a full read using an earlier weak match");
        Check(File.ReadAllLines(capture).Contains("--drive=1"), "Auto Read probe uses selected drive");
        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", null);
        app.Shutdown();
    }

    private static void RunUpdateChecks(MainWindow window)
    {
        var coffee = Control<Button>(window, "BtnBuyMeACoffee");
        var check = Control<Button>(window, "BtnCheckForUpdates");
        var banner = Control<Border>(window, "UpdateBanner");
        var update = Control<Button>(window, "BtnDoUpdate");
        var message = Control<TextBlock>(window, "TxtUpdateMsg");
        var header = (StackPanel)coffee.Parent;
        Check(header.Children.Contains(Control<Button>(window, "BtnMore")) && header.Children.Contains(coffee),
            "support is directly visible in the header beside More options");
        Check(((StackPanel)check.Parent).Children.Contains(check) && !header.Children.Contains(check),
            "manual updates remain in More options");
        Equal("Check for Updates", check.Content?.ToString() ?? "", "manual update button label");

        string version = UpdateChecker.CurrentAppVersion();
        var current = new GhRelease(version, "https://example.invalid/current.exe", "current.exe");
        Invoke(window, "ApplyUpdateCheckResults", current, null!, "", false);
        Check(banner.Visibility == Visibility.Collapsed, "automatic no-update checks stay quiet");
        Invoke(window, "ApplyUpdateCheckResults", current, null!, "", true);
        Check(message.Text.Contains("is up to date") && banner.Visibility == Visibility.Visible,
            "manual current-version check gives visible feedback");
        Check(update.Visibility == Visibility.Collapsed, "current version has no install action");
        Invoke(window, "ApplyUpdateCheckResults", null!, null!, "1.0", true);
        Check(message.Text.Contains("unavailable") && !message.Text.Contains("up to date"),
            "failed requests do not claim the installation is current");
        Check(update.Visibility == Visibility.Collapsed, "failed checks have no stale install action");

        var newer = new GhRelease("99.0.0", "https://example.invalid/new.exe", "new.exe", "https://example.invalid/product");
        var gw = new GhRelease("2.0", "https://example.invalid/gw.zip", "gw.zip");
        Invoke(window, "ApplyUpdateCheckResults", newer, gw, "1.0", true);
        Check(update.Visibility == Visibility.Visible && update.IsEnabled, "new versions retain Update Now");
        Equal(newer.DownloadUrl, Field<string>(window, "_pendingAppExeUrl"), "app download passed to existing updater");
        Equal(gw.DownloadUrl, Field<string>(window, "_pendingGwZipUrl"), "GW download passed to existing updater");
        Invoke(window, "ApplyUpdateCheckResults", current, null!, "", true);
        Check(typeof(MainWindow).GetField("_pendingAppExeUrl", PrivateInstance)!.GetValue(window) == null,
            "current result clears a stale pending download");

        banner.Visibility = Visibility.Collapsed;
        Control<TextBlock>(window, "TxtTitleVersion").Text = "v" + version;
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.MinWidth, window.MinHeight));
        content.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
        content.UpdateLayout();
        var more = Control<Button>(window, "BtnMore");
        var morePoint = more.TranslatePoint(new Point(), content);
        var title = Control<TextBlock>(window, "TxtTitleVersion");
        var titlePoint = title.TranslatePoint(new Point(), content);
        var coffeePoint = coffee.TranslatePoint(new Point(), content);
        Check(coffee.Visibility == Visibility.Visible && coffee.ActualWidth >= 120 && coffee.ActualHeight >= 32 &&
            coffeePoint.X >= titlePoint.X + title.ActualWidth &&
            coffeePoint.X + coffee.ActualWidth <= morePoint.X,
            "support button fits visibly between title and header icons at minimum width");
        Check(more.ActualWidth > 0, "More options is visible");
        Check(morePoint.X >= titlePoint.X + title.ActualWidth &&
            morePoint.X + more.ActualWidth <= window.MinWidth,
            "header controls do not overlap the title at the minimum window width");
        string? evidence = Environment.GetEnvironmentVariable("HAMSTERWEAZLE_SMOKE_EVIDENCE_DIR");
        if (Environment.GetEnvironmentVariable("HAMSTERWEAZLE_SMOKE_LIVE_UPDATES") == "1")
        {
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<bool>(window, "_checkingForUpdates") && !check.IsEnabled,
                "one click immediately starts the real update check");
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (Field<bool>(window, "_checkingForUpdates"))
            {
                Check(DateTime.UtcNow < deadline, "live update check timeout");
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
            Check(check.IsEnabled && banner.Visibility == Visibility.Visible &&
                (message.Text.Contains("is up to date") || message.Text.Contains("available")) &&
                !message.Text.Contains("unavailable"), "live update check completes with an explicit result");
            if (!string.IsNullOrEmpty(evidence)) File.WriteAllText(Path.Combine(evidence, "live-update-check.txt"), message.Text);
            banner.Visibility = Visibility.Collapsed;
            content.UpdateLayout();
        }
        if (!string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence);
            foreach (double scale in new[] { 1.0, 1.5 })
            {
                var bitmap = new RenderTargetBitmap((int)(window.MinWidth * scale),
                    (int)(window.MinHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(evidence, $"main-minimum-{scale * 100:0}pct.png"));
                encoder.Save(output);
            }
        }
    }

    private static void RunWorkbenchChecks(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var activity = Control<Expander>(window, "ActivityExpander");
        var map = Control<CheckBox>(window, "ChkTrackMap");
        map.IsChecked = false;
        Check(!activity.IsExpanded && !Control<Expander>(window, "AdvExpander").IsExpanded,
            "secondary controls start collapsed");
        Control<CheckBox>(window, "ChkCustomOutput").IsChecked = false;
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabRead").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Control<TextBox>(window, "TxtFile").IsReadOnly && Control<StackPanel>(window, "PanelFile").Visibility == Visibility.Visible,
            "generated Inbox destination stays visible and protected");
        Equal("Ready", Control<TextBlock>(window, "DriveStatusText").Text, "plain idle status");
        foreach (var (name, heading) in new[] { ("TabRead", "READ"), ("TabWrite", "WRITE"),
            ("TabErase", "ERASE"), ("TabTools", "TOOLS"), ("TabInfo", "DEVICE") })
        {
            var tab = Control<System.Windows.Controls.Primitives.ToggleButton>(window, name);
            tab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(heading, Control<TextBlock>(window, "TxtOperationKind").Text, "operation rail uses a single compact label");
            Check(new[] { "TabRead", "TabWrite", "TabErase", "TabTools", "TabInfo" }
                .Count(n => Control<System.Windows.Controls.Primitives.ToggleButton>(window, n).IsChecked == true) == 1,
                "operation rail keeps one selection");
        }
        Invoke(window, "SetRunning", true);
        Check(activity.IsExpanded, "requested device output opens Activity");
        Invoke(window, "SetRunning", false);
        Invoke(window, "BtnClearLog_Click", new Button(), new RoutedEventArgs());
        activity.IsExpanded = false;
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabRead").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Invoke(window, "SetRunning", true);
        Check(Control<Button>(window, "BtnRun").Visibility == Visibility.Collapsed &&
            Control<Button>(window, "BtnCancel").Visibility == Visibility.Visible &&
            !Control<ComboBox>(window, "CboFormat").IsEnabled, "running job presents Stop and locks the operation settings");
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabWrite").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal("READ", Control<TextBlock>(window, "TxtOperationKind").Text, "busy job cannot change operation");
        Invoke(window, "SetRunning", false);
        Invoke(window, "BtnClearLog_Click", new Button(), new RoutedEventArgs());
        Invoke(window, "AppendLog", "T0.0: IBM MFM (18/18 sectors)");
        Check(Control<TextBox>(window, "TxtLog").Text.Contains("18/18") && !activity.IsExpanded,
            "ordinary output is available without opening Activity");
        Invoke(window, "AppendLog", "[error] Test issue");
        Check(!activity.IsExpanded && activity.Header.ToString()!.Contains("Needs attention") && Control<TextBlock>(window, "TxtActivitySummary").Text == "Needs attention",
            "issues flag Activity without forcing it open or moving the work area");
        Invoke(window, "BtnClearLog_Click", new Button(), new RoutedEventArgs());
        activity.IsExpanded = false;
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "LibraryQueueTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Control<ScrollViewer>(window, "InboxScroll").Visibility == Visibility.Collapsed &&
            Control<ScrollViewer>(window, "QueueScroll").Visibility == Visibility.Visible, "library switches to queue without showing both lists");
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "LibraryInboxTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        string evidence = Environment.GetEnvironmentVariable("HAMSTERWEAZLE_SMOKE_EVIDENCE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "capture-ui-evidence");
        var settings = Field<AppSettings>(window, "_settings");
        settings.InboxDir = Path.Combine(evidence, "workbench-inbox");
        Directory.CreateDirectory(settings.InboxDir);
        string disk = Path.Combine(settings.InboxDir, "HamsterOS.img");
        if (!File.Exists(disk)) Fat12Image.Create("ibm.1440").SaveCopy(disk);
        Invoke(window, "RefreshInbox");
        var card = (Border)Control<StackPanel>(window, "InboxPanel").Children[0];
        Check(card.ContextMenu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "Use image for writing"),
            "quiet library preserves file operations in its action menu");
        card.ContextMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Use image for writing")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var dispatchFrame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => dispatchFrame.Continue = false));
        Dispatcher.PushFrame(dispatchFrame);
        Check(Control<TextBlock>(window, "TxtOperationKind").Text == "WRITE" &&
            Control<TextBox>(window, "TxtFile").Text == disk && !Field<bool>(window, "_driveIsRunning"),
            "image menu selects Write without starting the drive");
        var queueCard = (Border)Invoke(window, "BuildQueueCard", new WriteQueueItem { FilePath = disk, Format = "ibm.1440" })!;
        Check(((StackPanel)queueCard.Child).Children.OfType<Button>().Any(b => b.Content is TextBlock t && t.Text == "HamsterOS.img"),
            "queue filename opens image details");
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabRead").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        foreach (string theme in new[] { "Dark", "Light", "Amiga" })
        {
            App.SwitchTheme(theme);
            foreach (var size in new[] { new Size(1040, 700), new Size(1240, 860) })
            {
                content.Measure(size); content.Arrange(new Rect(new Point(), size)); content.UpdateLayout();
                DependencyObject parent = Control<Button>(window, "BtnRun");
                while (parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
                ((ScrollViewer)parent).ScrollToHome(); content.UpdateLayout();
                var bezel = Control<Border>(window, "DriveBezel");
                Check(Math.Abs(bezel.ActualWidth - 400) < 0.1 && Math.Abs(bezel.ActualHeight - 116) < 0.1,
                    $"drive keeps its fixed size when the window resizes: {theme}, {size}");
                var run = Control<Button>(window, "BtnRun");
                Point point = run.TranslatePoint(new Point(), content);
                Check(run.ActualWidth >= 120 && point.Y + run.ActualHeight <= size.Height - 36,
                    $"primary action fits without scrolling: {theme}, {size}");
                var format = Control<ComboBox>(window, "CboFormat");
                Point formatPoint = format.TranslatePoint(new Point(), content);
                var library = Control<ScrollViewer>(window, "InboxScroll");
                Check(formatPoint.X + format.ActualWidth < library.TranslatePoint(new Point(), content).X,
                    "format picker does not overlap the image library");
                var workspace = Control<ScrollViewer>(window, "WorkspaceScroll");
                double workspaceHeight = workspace.ActualHeight;
                Check(workspace.ScrollableHeight < 1, "collapsed Read controls fit without a page scrollbar");
                Invoke(window, "AppendLog", string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Output line {i:000}: recorded tool activity")));
                activity.IsExpanded = true; content.UpdateLayout();
                var log = Control<ScrollViewer>(window, "LogScroll");
                Check(Math.Abs(workspace.ActualHeight - workspaceHeight) < 0.1 &&
                    Math.Abs(run.TranslatePoint(new Point(), content).Y - point.Y) < 0.1 && workspace.ScrollableHeight < 1,
                    $"opening Activity leaves operation controls and page scrolling unchanged: {theme}, {size}");
                Check(log.ViewportHeight > 10 && !double.IsInfinity(log.ViewportHeight) && log.ScrollableHeight > 100,
                    "long output scrolls inside its own bounded Activity pane");
                log.ScrollToEnd(); content.UpdateLayout();
                Check(log.VerticalOffset > 0, "Activity output can scroll independently");
                if (theme == "Dark")
                {
                    var activityBitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    activityBitmap.Render(content); var activityEncoder = new PngBitmapEncoder(); activityEncoder.Frames.Add(BitmapFrame.Create(activityBitmap));
                    using var activityOutput = File.Create(Path.Combine(evidence, $"workbench-activity-{size.Width:0}.png")); activityEncoder.Save(activityOutput);
                }
                activity.IsExpanded = false;
                Invoke(window, "BtnClearLog_Click", new Button(), new RoutedEventArgs());
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(evidence, $"workbench-{theme}-{size.Width:0}.png")); encoder.Save(output);
            }
        }
        App.SwitchTheme("Dark");
        var themeButton = Control<Button>(window, "BtnTheme");
        themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal("Light", Application.Current.Resources["Win.ThemeName"]?.ToString() ?? "", "appearance button switches to light");
        themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal("Dark", SettingsManager.Load().Theme, "appearance button persists dark on the next click");
        RunSettingsChecks(window);
    }

    private static void RunSettingsChecks(MainWindow window)
    {
        var settingsTab = Control<System.Windows.Controls.Primitives.ToggleButton>(window, "BtnSettings");
        var tools = Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabTools");
        var rail = (StackPanel)tools.Parent;
        Check(rail.Children.IndexOf(settingsTab) == rail.Children.IndexOf(tools) + 1,
            "Settings appears directly beneath Tools in the rail");
        int windowCount = Application.Current.Windows.Count;
        string imagePath = Control<TextBox>(window, "TxtFile").Text;
        settingsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var view = (SettingsView)Control<ContentControl>(window, "SettingsHost").Content;
        Check(Application.Current.Windows.Count == windowCount && settingsTab.IsChecked == true &&
            Control<Grid>(window, "SettingsWorkspace").Visibility == Visibility.Visible &&
            Control<Grid>(window, "WorkbenchLayout").Visibility == Visibility.Collapsed,
            "Settings opens inline without creating a modal window");
        Check(new[] { "TabRead", "TabWrite", "TabErase", "TabTools", "TabInfo" }
            .All(n => Control<System.Windows.Controls.Primitives.ToggleButton>(window, n).IsChecked != true),
            "Settings owns the rail selection");
        ((RadioButton)view.FindName("RbLight")).IsChecked = true;
        Check(SettingsManager.Load().Theme == "Light" && Field<AppSettings>(window, "_settings").Theme == "Light",
            "inline appearance changes persist and update main-window settings immediately");
        Control<Button>(window, "BtnTheme").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((RadioButton)view.FindName("RbDark")).IsChecked == true,
            "header appearance and inline appearance remain synchronized");
        var memory = Field<AppSettings>(window, "_settings");
        string previousOutputDir = memory.LastOutputDir;
        memory.LastOutputDir = "unsaved-operation-choice";
        var saved = SettingsManager.Load();
        string? previousGwPath = saved.GwPath;
        saved.GwPath = Path.Combine(AppContext.BaseDirectory, "selected-host-tool.exe");
        typeof(SettingsView).GetMethod("SaveChanges", PrivateInstance)!.Invoke(view, [saved]);
        Check(Field<AppSettings>(window, "_settings").GwPath == saved.GwPath &&
            Field<GwRunner>(window, "_runner").GwPath == saved.GwPath &&
            SettingsManager.Load().LastOutputDir == "unsaved-operation-choice",
            "inline changes update the runner without overwriting an in-memory operation choice");
        saved = SettingsManager.Load(); saved.GwPath = previousGwPath;
        memory.LastOutputDir = previousOutputDir;
        typeof(SettingsView).GetMethod("SaveChanges", PrivateInstance)!.Invoke(view, [saved]);

        var content = (FrameworkElement)window.Content;
        ((Expander)view.FindName("AboutExpander")).IsExpanded = true;
        content.Measure(new Size(1040, 700)); content.Arrange(new Rect(0, 0, 1040, 700)); content.UpdateLayout();
        var scroll = (ScrollViewer)view.FindName("SettingsScroll");
        Check(scroll.ViewportHeight > 100 && scroll.ScrollableHeight > 0 &&
            scroll.ActualHeight <= Control<Grid>(window, "SettingsWorkspace").ActualHeight,
            "long Settings content scrolls within its own workspace");
        scroll.ScrollToEnd(); content.UpdateLayout();
        Check(scroll.VerticalOffset > 0, "Settings scroll reaches the About section");
        ((Expander)view.FindName("AboutExpander")).IsExpanded = false;
        typeof(SettingsView).GetMethod("SetInstalling", PrivateInstance)!.Invoke(view, [true]);
        Check(!Control<Border>(window, "WorkbenchRail").IsEnabled && !Control<Border>(window, "DiskLibrary").IsEnabled,
            "software installation temporarily locks operations and image actions");
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabRead").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settingsTab.IsChecked == true, "software installation cannot switch into a disk job");
        typeof(SettingsView).GetMethod("SetInstalling", PrivateInstance)!.Invoke(view, [false]);
        Check(Control<Border>(window, "WorkbenchRail").IsEnabled && Control<Border>(window, "DiskLibrary").IsEnabled,
            "operation navigation returns after installation finishes");
        Control<System.Windows.Controls.Primitives.ToggleButton>(window, "TabRead").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settingsTab.IsChecked != true && Control<Grid>(window, "WorkbenchLayout").Visibility == Visibility.Visible &&
            Control<TextBox>(window, "TxtFile").Text == imagePath,
            "returning from Settings preserves the selected operation and image");
        Invoke(window, "SetRunning", true);
        settingsTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!settingsTab.IsEnabled && Control<Grid>(window, "SettingsWorkspace").Visibility == Visibility.Collapsed,
            "a running disk job cannot enter Settings");
        Invoke(window, "SetRunning", false);
    }

    private static void RunInboxChecks(MainWindow window, string work)
    {
        string inbox = Path.Combine(work, "inbox");
        Directory.CreateDirectory(inbox);
        string[] images = ["disk.IMG", "disk.dmk", "disk.scp", "disk00.0.raw", "disk.mfm", "disk.adf"];
        foreach (string name in images) File.WriteAllBytes(Path.Combine(inbox, name), [0]);
        string image = Path.Combine(inbox, "disk.IMG");
        var report = new CaptureReport { ImagePath = image, Format = "ibm.1440" };
        report.Finish(0, false);
        CaptureStore.Save(report);
        File.WriteAllText(image + ".capture-pending.json.tmp", "incomplete report");
        File.WriteAllText(Path.Combine(inbox, "notes.txt"), "notes");
        var settings = Field<AppSettings>(window, "_settings");
        settings.InboxDir = inbox;
        Invoke(window, "RefreshInbox");
        Check(images.Length == Control<StackPanel>(window, "InboxPanel").Children.Count,
            "Inbox shows disk images and excludes JSON/TXT reports, notes and temporary files");
        Check(CaptureStore.FindForImage(image) != null, "hidden capture reports remain available for rescue");
        Check(Directory.GetFiles(inbox).Length == images.Length + 4, "Inbox filtering preserves every file on disk");
        settings.DismissedInboxFiles.Add(image);
        Invoke(window, "RefreshInbox");
        Check(images.Length - 1 == Control<StackPanel>(window, "InboxPanel").Children.Count,
            "image dismissal still works with hidden reports");
    }

    private static void RunCaptureUiChecks(MainWindow window)
    {
        Check(!new AppSettings().ShowTrackMap, "track map defaults off");
        var toggle = Control<CheckBox>(window, "ChkTrackMap");
        var panel = Control<Border>(window, "TrackMapPanel");
        toggle.IsChecked = false;
        Check(panel.Visibility == Visibility.Collapsed, "hidden map consumes no layout space");
        var report = new CaptureReport { Format = "ibm.1440", Layout = DiskLayout.ForFormat("ibm.1440") };
        for (int c = 0; c < 80; c++) for (int h = 0; h < 2; h++)
        {
            int found = c == 12 && h == 1 ? 9 : 18;
            report.Feed($"T{c}.{h}: IBM MFM ({found}/18 sectors) from Raw Flux (135692 flux in 400.30ms)");
            if (c == 20 && h == 0) report.Feed($"T{c}.{h}: IBM MFM (18/18 sectors) from Raw Flux (203248 flux in 600.20ms) (Retry #1.1)");
        }
        report.Finished = DateTimeOffset.UtcNow;
        typeof(MainWindow).GetField("_capture", PrivateInstance)!.SetValue(window, report);
        toggle.IsChecked = true;
        Invoke(window, "RenderTrackMap");
        Check(panel.Visibility == Visibility.Visible && Control<StackPanel>(window, "TrackMapRows").Children.Count == 2, "map toggle creates two track rows");
        Check(Control<TextBlock>(window, "TrackMapSummary").Text.Contains("1 incomplete tracks"), "map retains final incomplete result");
        string evidence = Environment.GetEnvironmentVariable("HAMSTERWEAZLE_SMOKE_EVIDENCE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "capture-ui-evidence");
        Directory.CreateDirectory(evidence);
        Invoke(window, "LoadFormats");
        Invoke(window, "SelectFormat", "ibm.1440", "IBM PC");
        Invoke(window, "UpdateTabUI");
        foreach (string theme in new[] { "Dark", "Light", "Amiga" })
        {
            App.SwitchTheme(theme);
            foreach (bool visible in new[] { false, true })
            {
                toggle.IsChecked = visible;
                foreach (double width in new[] { 1040d, 1240d })
                {
                    var content = (FrameworkElement)window.Content;
                    double height = width == 1040 ? 700 : 860;
                    content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                    if (visible)
                    {
                        // Optional diagnostics can extend the native scrolling workspace.
                        DependencyObject ancestor = panel;
                        while (ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
                        ((ScrollViewer)ancestor).ScrollToEnd(); content.UpdateLayout();
                        Point top = panel.TranslatePoint(new Point(), content);
                        Check(panel.ActualHeight > 0 && top.Y + panel.ActualHeight <= height + 0.5, $"map fits the minimum window: theme={theme}, width={width}, top={top.Y}, height={panel.ActualHeight}, available={height}");
                    }
                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(evidence, $"{theme}-map-{(visible ? "on" : "off")}-{width:0}.png")); encoder.Save(file);
                }
            }
        }
        App.SwitchTheme("Dark"); toggle.IsChecked = false;
        Check(panel.Visibility == Visibility.Collapsed, "map can be hidden again");
        Field<AppSettings>(window, "_settings").ShowTrackMap = true;
        SettingsManager.Save(Field<AppSettings>(window, "_settings"));
        string imagePath = Path.Combine(evidence, "browser-preview.img");
        string hostFile = Path.Combine(evidence, "README.TXT");
        File.WriteAllText(hostFile, "HamsterWeazle image tools preview.");
        var image = Fat12Image.Create("ibm.1440"); image.ImportFiles([hostFile]);
        if (File.Exists(imagePath)) File.Delete(imagePath);
        image.SaveCopy(imagePath);
        foreach (string theme in new[] { "Dark", "Light", "Amiga" })
        {
            App.SwitchTheme(theme);
            var dialog = (ImageToolsDialog)Activator.CreateInstance(typeof(ImageToolsDialog), PrivateInstance, null,
                [imagePath, (Action)(() => { })], null)!;
            var files = (ListBox)typeof(ImageToolsDialog).GetField("_files", PrivateInstance)!.GetValue(dialog)!;
            Check(files.Items.Count == 1, "image browser loads files without HxC");
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(700, 350)); content.Arrange(new Rect(0, 0, 700, 350)); content.UpdateLayout();
            Check(files.ActualHeight > 100, "image browser leaves room for files at minimum size");
            var bitmap = new RenderTargetBitmap(700, 350, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(evidence, $"{theme}-image-browser.png")); encoder.Save(file);
            dialog.Close();
        }
        App.SwitchTheme("Dark");
        Check(SettingsManager.Load().ShowTrackMap, "map preference persists");
        Field<AppSettings>(window, "_settings").ShowTrackMap = false;
        SettingsManager.Save(Field<AppSettings>(window, "_settings"));
    }

    private static void RunDriveChecks(MainWindow window, string capture, string file)
    {
        File.WriteAllBytes(file, new byte[1474560]); // Valid raw image size; tools still only record arguments.
        var drive = Control<ComboBox>(window, "CboDrive");
        foreach (string? selected in new string?[] { "B", null, "A", "0", "1", "2", "3" })
        {
            drive.SelectedItem = drive.Items.OfType<ComboBoxItem>().Single(x => (string?)x.Tag == selected);
            foreach (var operation in new[] { GwOperation.Read, GwOperation.Write, GwOperation.Erase, GwOperation.Info })
            {
                Select(window, operation, "ibm.1440", file);
                string? expectedDrive = operation == GwOperation.Info ? null : selected;
                string preview = Control<TextBlock>(window, "TxtCmdPreview").Text;
                Check(!preview.Contains("--drive "), "drive preview uses equals syntax");
                Check(expectedDrive == null ? !preview.Contains("--drive") : preview.Contains("--drive=" + expectedDrive), "selected drive in preview");
                RunButton(window);
                AssertDriveArguments(capture, operation.ToString().ToLowerInvariant(), expectedDrive);
            }

            Invoke(window, "BtnCleanDrive_Click", new Button(), new RoutedEventArgs());
            WaitForOperation(window);
            AssertDriveArguments(capture, "clean", selected);
        }

        var queued = new WriteQueueItem { FilePath = file, Format = "ibm.1440", Vendor = "IBM PC", Drive = "B", DevicePort = "COM17" };
        Invoke(window, "QuickWrite_Click", new Button { Tag = queued }, new RoutedEventArgs());
        WaitForOperation(window);
        AssertDriveArguments(capture, "write", "B");
    }

    private static void AssertDriveArguments(string capture, string operation, string? drive)
    {
        string[] actual = File.ReadAllLines(capture);
        Equal("gw.exe", actual[0], "Greaseweazle tool");
        Equal(operation, actual[1], "Greaseweazle operation");
        string[] driveArgs = actual.Where(x => x.StartsWith("--drive", StringComparison.Ordinal)).ToArray();
        Equal(drive == null ? "" : "--drive=" + drive, string.Join(" ", driveArgs), "selected drive is one equals argument");
        int deviceIndex = Array.IndexOf(actual, "--device");
        Check(deviceIndex >= 0 && actual[deviceIndex + 1] == "COM17", "selected controller reaches the tool");
    }

    private static void Select(MainWindow window, GwOperation operation, string? format, string path)
    {
        typeof(MainWindow).GetField("_currentOp", PrivateInstance)!.SetValue(window, operation);
        var combo = Control<ComboBox>(window, "CboFormat");
        combo.Items.Clear();
        if (format != null)
        {
            combo.Items.Add(new DiskFormat("Test", format, format));
            combo.SelectedIndex = 0;
        }
        Control<TextBox>(window, "TxtFile").Text = path;
        Invoke(window, "UpdateCommandPreview");
    }

    private static void RunButton(MainWindow window, bool expectError = false)
    {
        Invoke(window, "BtnRun_Click", Control<Button>(window, "BtnRun"), new RoutedEventArgs());
        WaitForOperation(window, expectError);
    }

    private static void WaitForOperation(MainWindow window, bool expectError = false)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (Field<bool>(window, "_driveIsRunning"))
        {
            Check(DateTime.UtcNow < deadline, "process timeout");
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Check(Field<bool>(window, "_driveHasError") == expectError, "operation error status");
    }

    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
    private static void Equal(string expected, string actual, string name) => Check(expected == actual, $"{name}\nExpected: {expected}\nActual: {actual}");
    private static void EqualLines(string[] expected, string capture) => Equal(string.Join("\n", expected), string.Join("\n", File.ReadAllLines(capture)), "process argument tokens");
}
