using System.Diagnostics;

namespace HamsterWeazle.Services;

public enum DmkOperation { Read, Write }

public sealed record DmkOptions(string? Device = null, string? Drive = null, bool ShugartBus = true);

/// <summary>Runs qbarnes/gw2dmk tools for TRS-80 and other WD17xx DMK disks.</summary>
public sealed class DmkRunner
{
    public string? ToolDirectory { get; set; }
    private Process? _process;
    private CancellationTokenSource? _cts;
    public event Action<string>? OutputReceived;

    public string? Gw2DmkPath => FindTool("gw2dmk");
    public string? Dmk2GwPath => FindTool("dmk2gw");
    public bool IsInstalled => Gw2DmkPath != null && Dmk2GwPath != null;
    public string LogPath => GetLogPath(DmkOperation.Read);

    public string GetLogPath(DmkOperation operation, bool console = false)
    {
        string? tool = operation == DmkOperation.Read ? Gw2DmkPath : Dmk2GwPath;
        string directory = tool != null ? Path.GetDirectoryName(tool)!
            : !string.IsNullOrWhiteSpace(ToolDirectory) ? ToolDirectory
            : Path.Combine(AppContext.BaseDirectory, "gw2dmk");
        string name = operation == DmkOperation.Read ? "gw2dmk" : "dmk2gw";
        return Path.Combine(directory, name + (console ? "-last-console.log" : "-last.log"));
    }

    public string BuildArguments(DmkOperation operation, string filePath, DmkOptions options)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Device)) args.Add($"-G {Quote(options.Device)}");
        if (options.ShugartBus) args.Add("-B shugart");
        if (operation == DmkOperation.Read) args.Add("-s 1");
        // v0.5.1 leaves file verbosity at zero when only -u is supplied.
        args.Add(operation == DmkOperation.Read ? "-v 22" : "-v 11");
        args.Add($"-u {Quote(GetLogPath(operation))}");
        if (!string.IsNullOrWhiteSpace(options.Drive))
        {
            string drive = options.Drive.Equals("A", StringComparison.OrdinalIgnoreCase) ? "a"
                : options.Drive.Equals("B", StringComparison.OrdinalIgnoreCase) ? "b" : options.Drive;
            args.Add($"-d {Quote(drive)}");
        }
        args.Add(Quote(filePath));
        return string.Join(" ", args);
    }

    public async Task<int> RunAsync(DmkOperation operation, string filePath, DmkOptions options)
    {
        string? exe = operation == DmkOperation.Read ? Gw2DmkPath : Dmk2GwPath;
        if (exe == null) throw new InvalidOperationException("gw2dmk optional package is not installed.");
        using var cts = new CancellationTokenSource();
        using var consoleLog = new StreamWriter(GetLogPath(operation, console: true), append: false)
        { AutoFlush = true };
        var logLock = new object();
        void Record(string line)
        {
            lock (logLock) consoleLog.WriteLine(line);
        }
        Record($"[started {DateTimeOffset.Now:O}]");
        var psi = new ProcessStartInfo(exe, BuildArguments(operation, filePath, options))
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        Record($"$ {Quote(exe)} {psi.Arguments}");
        using var process = new Process { StartInfo = psi };
        _cts = cts;
        _process = process;
        try
        {
            process.Start();
            var stdout = PumpAsync(process.StandardOutput, Record);
            var stderr = PumpAsync(process.StandardError, Record);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
                Record("[cancelled]");
                throw;
            }
            await Task.WhenAll(stdout, stderr);
            Record($"[exit code {process.ExitCode}]");
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record($"[error] {ex.Message}");
            throw;
        }
        finally
        {
            _process = null;
            _cts = null;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }

    private string? FindTool(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        if (!string.IsNullOrWhiteSpace(ToolDirectory))
        {
            string p = Path.Combine(ToolDirectory, exe);
            if (File.Exists(p)) return p;
        }
        string local = Path.Combine(AppContext.BaseDirectory, "gw2dmk", exe);
        if (File.Exists(local)) return local;
        return null;
    }

    private async Task PumpAsync(StreamReader reader, Action<string> record)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            record(line);
            OutputReceived?.Invoke(line);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
