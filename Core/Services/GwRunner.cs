using System.Diagnostics;
using System.IO;

namespace HamsterWeazle.Services;

public enum GwOperation { Read, Write, Erase, Tools, Info, Clean }

public record GwOptions(
    int?   StartCyl   = null,
    int?   EndCyl     = null,
    int    Retries    = 3,
    bool   Verify     = false,
    bool   AdaptiveRetry = true,
    string? Drive     = null,
    int?   Revs       = null,
    string? DevicePort = null,
    bool   Raw        = false);

public class GwRunner
{
    public string? GwPath { get; set; }

    private Process? _process;
    private CancellationTokenSource? _cts;

    public event Action<string>? OutputReceived;
    public event Action<int>?    ProcessExited;
    public CaptureReport? LastReport { get; private set; }
    public event Action<CaptureReport>? ReportChanged;

    // ── Discovery ──────────────────────────────────────────────────────────

    public static Func<string?>? GwExeFinder { get; set; }
    public static string? FindGwExe() => GwExeFinder?.Invoke();

    public static async Task<string> GetVersionAsync(string gwPath)
    {
        try
        {
            var psi = new ProcessStartInfo(gwPath, "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            string err    = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            string combined = (output + err).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(combined, @"Host Tools:\s*(\S+)");
            return m.Success ? $"v{m.Groups[1].Value}" : "found";
        }
        catch { return "unknown"; }
    }

    // ── Command building ────────────────────────────────────────────────────

    public string BuildArguments(GwOperation op, string format, string filePath, GwOptions opts)
    {
        var args = new List<string>();

        args.Add(op switch
        {
            GwOperation.Read  => "read",
            GwOperation.Write => "write",
            GwOperation.Erase => "erase",
            GwOperation.Info  => "info",
            GwOperation.Clean => "clean",
            _ => "read"
        });

        if (op is GwOperation.Read or GwOperation.Write or GwOperation.Erase or GwOperation.Clean
            && !string.IsNullOrWhiteSpace(opts.Drive))
            args.Add($"--drive={opts.Drive}");

        if (!string.IsNullOrWhiteSpace(opts.DevicePort))
            args.Add($"--device {opts.DevicePort}");

        if (op is GwOperation.Read or GwOperation.Write or GwOperation.Erase)
        {
            if (op is GwOperation.Read or GwOperation.Write && !string.IsNullOrWhiteSpace(format))
                args.Add($"--format {format}");

            if (opts.StartCyl.HasValue || opts.EndCyl.HasValue)
                args.Add($"--tracks c={opts.StartCyl ?? 0}-{opts.EndCyl ?? 79}");

            if (op is GwOperation.Read or GwOperation.Write && opts.Retries != 3)
                args.Add($"--retries {opts.Retries}");

            if (op == GwOperation.Read && opts.Raw)
                args.Add("--raw");

            if (op == GwOperation.Write && !opts.Verify)
                args.Add("--no-verify");

            if (op is GwOperation.Read or GwOperation.Erase && opts.Revs.HasValue && opts.Revs.Value > 1)
                args.Add($"--revs {opts.Revs.Value}");

            if (op is GwOperation.Read or GwOperation.Write && !string.IsNullOrWhiteSpace(filePath))
                args.Add($"\"{filePath}\"");
        }

        return string.Join(" ", args);
    }

    // ── Execution ───────────────────────────────────────────────────────────

    public async Task<int> RunAsync(string arguments, string? reportImagePath = null, CaptureReport? resumeFrom = null)
    {
        if (string.IsNullOrEmpty(GwPath))
            throw new InvalidOperationException("gw.exe path is not configured.");
        if (_process != null) throw new InvalidOperationException("A GreaseWeazle operation is already running.");

        _cts = new CancellationTokenSource();

        var psi = new ProcessStartInfo(GwPath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var report = CaptureReport.FromCommand(arguments, reportImagePath);
        string? commandImage = report?.Operation == "write" ? CaptureReport.FromCommand(arguments)?.ImagePath : null;
        if (report != null && resumeFrom != null) report.ContinueFrom(resumeFrom);
        if (report != null) { LastReport = report; ReportChanged?.Invoke(report); }
        int? exitCode = null;
        bool cancelled = false;
        Task stdoutTask = Task.CompletedTask, stderrTask = Task.CompletedTask;
        try
        {
            if (commandImage != null && FormatGuesser.WriteProblem(commandImage, report!.Format) is { } problem)
                throw new InvalidDataException(problem);
            _process.Start();
            stdoutTask = PumpOutputAsync(_process.StandardOutput, report, CancellationToken.None);
            stderrTask = PumpOutputAsync(_process.StandardError, report, CancellationToken.None);
            await _process.WaitForExitAsync(_cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            exitCode = _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { _process.Kill(entireProcessTree: true); } catch { }
            await _process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
        catch (Exception ex) { report?.Feed("[error] " + ex.Message); if (report != null) report.Fatal = true; throw; }
        finally
        {
            if (report != null)
            {
                try { await Task.Run(() => { report.Finish(exitCode, cancelled); CaptureStore.Save(report); }); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { OutputReceived?.Invoke("[warn] Capture report could not be saved: " + ex.Message); }
                ReportChanged?.Invoke(report);
            }
            _process.Dispose();
            _process = null;
            _cts.Dispose();
            _cts = null;
        }
        ProcessExited?.Invoke(exitCode!.Value);
        return exitCode.Value;
    }

    private async Task PumpOutputAsync(TextReader reader, CaptureReport? report, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (report?.Feed(line) == true) ReportChanged?.Invoke(report);
            OutputReceived?.Invoke(line);
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }
}
