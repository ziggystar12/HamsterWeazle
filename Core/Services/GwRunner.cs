using System.Diagnostics;
using System.IO;

namespace HamsterWeazle.Services;

public enum GwOperation { Read, Write, Erase, Tools, Info }

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
            _ => "read"
        });

        if (!string.IsNullOrWhiteSpace(opts.Drive))
            args.Add($"--drive {opts.Drive}");

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

    public async Task<int> RunAsync(string arguments)
    {
        if (string.IsNullOrEmpty(GwPath))
            throw new InvalidOperationException("gw.exe path is not configured.");

        _cts = new CancellationTokenSource();

        var psi = new ProcessStartInfo(GwPath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.Start();
        var stdoutTask = PumpOutputAsync(_process.StandardOutput, _cts.Token);
        var stderrTask = PumpOutputAsync(_process.StandardError, _cts.Token);

        await _process.WaitForExitAsync(_cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        ProcessExited?.Invoke(_process.ExitCode);
        return _process.ExitCode;
    }

    private async Task PumpOutputAsync(TextReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            OutputReceived?.Invoke(line);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }
}
