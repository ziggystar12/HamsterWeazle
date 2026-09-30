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
        Check(header.Children.IndexOf(check) == header.Children.IndexOf(coffee) + 1,
            "support link is immediately left of Check for Updates");
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
        var coffeePoint = coffee.TranslatePoint(new Point(), content);
        var checkPoint = check.TranslatePoint(new Point(), content);
        var title = Control<TextBlock>(window, "TxtTitleVersion");
        var titlePoint = title.TranslatePoint(new Point(), content);
        Check(coffee.ActualWidth > 0 && check.ActualWidth > 0, "both header controls are laid out");
        Check(coffeePoint.X >= titlePoint.X + title.ActualWidth &&
            checkPoint.X >= coffeePoint.X + coffee.ActualWidth &&
            checkPoint.X + check.ActualWidth <= window.MinWidth,
            "title and update controls do not overlap at the minimum window width");
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
        foreach (string theme in new[] { "Dark", "Amiga" })
        {
            App.SwitchTheme(theme);
            foreach (bool visible in new[] { false, true })
            {
                toggle.IsChecked = visible;
                foreach (double width in new[] { 1100d, 1500d })
                {
                    var content = (FrameworkElement)window.Content;
                    double height = width == 1100 ? 650 : 760;
                    content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                    if (visible)
                    {
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
        foreach (string theme in new[] { "Dark", "Amiga" })
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
