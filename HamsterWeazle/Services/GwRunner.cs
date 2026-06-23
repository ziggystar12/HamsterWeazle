using System.Diagnostics;
using System.IO;

namespace HamsterWeazle.Services;

public enum GwOperation { Read, Write, Erase, Info }

public record GwOptions(
    int?   StartCyl = null,
    int?   EndCyl   = null,
    int    Retries  = 3,
    bool   Verify   = false,
    string? Drive   = null);

public class GwRunner
{
    public string? GwPath { get; set; }

    private Process? _process;
    private CancellationTokenSource? _cts;

    public event Action<string>? OutputReceived;
    public event Action<int>?    ProcessExited;

    // ── Discovery ──────────────────────────────────────────────────────────

    public static string? FindGwExe()
    {
        // 1. Same folder as this exe
        string exeDir = AppContext.BaseDirectory;
        foreach (string name in new[] { "gw.exe", "gw" })
        {
            string path = Path.Combine(exeDir, name);
            if (File.Exists(path)) return path;
        }

        // 2. greaseweazle/ subfolder (our default download location)
        string gwSubDir = Path.Combine(exeDir, "greaseweazle", "gw.exe");
        if (File.Exists(gwSubDir)) return gwSubDir;

        // 3. Scan PATH
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            string path = Path.Combine(dir.Trim(), "gw.exe");
            if (File.Exists(path)) return path;
        }

        return null;
    }

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

        if (op is GwOperation.Read or GwOperation.Write)
        {
            if (!string.IsNullOrWhiteSpace(format))
                args.Add($"--format {format}");

            if (opts.StartCyl.HasValue || opts.EndCyl.HasValue)
                args.Add($"--tracks c={opts.StartCyl ?? 0}-{opts.EndCyl ?? 79}");

            if (opts.Retries != 3)
                args.Add($"--retries {opts.Retries}");

            if (op == GwOperation.Write && opts.Verify)
                args.Add("--verify");

            if (!string.IsNullOrWhiteSpace(filePath))
                args.Add($"\"{filePath}\"");
        }

        return string.Join(" ", args);
    }

    // ── Execution ───────────────────────────────────────────────────────────

    public async Task RunAsync(string arguments)
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

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line) OutputReceived?.Invoke(line);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) OutputReceived?.Invoke(line);
        };
        _process.Exited += (_, _) => ProcessExited?.Invoke(_process.ExitCode);

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await _process.WaitForExitAsync(_cts.Token);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }
}
