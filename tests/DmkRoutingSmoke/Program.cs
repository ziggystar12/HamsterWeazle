using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
            Console.WriteLine("PASS: DMK preview, main/queue routing, quoting, persistent diagnostics, read failures, and inbox naming.");
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
        var settings = Field<AppSettings>(window, "_settings");
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
        Environment.SetEnvironmentVariable("HAMSTERWEAZLE_DMK_SMOKE_MODE", null);
        app.Shutdown();
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
