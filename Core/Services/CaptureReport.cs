using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HamsterWeazle.Services;

public enum TrackState { Pending, Reading, Read, Retried, Missing, HeaderMismatch, Written, Verified, Failed, Unknown }
public sealed class TrackCapture
{
    public int Cylinder { get; set; }
    public int Head { get; set; }
    public int Found { get; set; }
    public int Expected { get; set; }
    public int Attempts { get; set; }
    public bool GaveUp { get; set; }
    public bool UnexpectedHeader { get; set; }
    public bool Written { get; set; }
    public bool VerifyFailed { get; set; }
    public TrackState State(bool finished, bool verified) => VerifyFailed ? TrackState.Failed
        : UnexpectedHeader ? TrackState.HeaderMismatch
        : Written ? verified ? TrackState.Verified : TrackState.Written
        : Expected > 0 && Found == Expected ? Attempts > 1 ? TrackState.Retried : TrackState.Read
        : GaveUp || (finished && Expected > Found) ? TrackState.Missing
        : finished ? TrackState.Unknown : TrackState.Reading;
}
public sealed record TrackView(int Cylinder, int Head, TrackState State, int Found, int Expected, int Attempts);

public sealed class CaptureReport
{
    private readonly object _gate = new();
    private static readonly Regex Track = new(@"^\s*T(\d+)\.(\d+):\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex Sectors = new(@"\((\d+)/(\d+) sectors\)", RegexOptions.Compiled);
    private static readonly Regex MapRow = new(@"^\s*(\d+)\.\s*(\d+):[ \t]([.Xx -]+)$", RegexOptions.Compiled);
    private string? _mapTens;
    public int SchemaVersion { get; set; } = 1;
    public string Operation { get; set; } = "read";
    public string Format { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }
    public int? ExitCode { get; set; }
    public bool Cancelled { get; set; }
    public bool Verified { get; set; }
    public bool Fatal { get; set; }
    public string ImageSha256 { get; set; } = "";
    public long ImageLength { get; set; }
    public DiskLayout? Layout { get; set; }
    public Dictionary<string, TrackCapture> Tracks { get; set; } = [];
    public Dictionary<string, string> SectorRows { get; set; } = [];
    public int[]? MapCylinders { get; set; }
    public List<string> Log { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    public List<string> PreviousReports { get; set; } = [];
    public bool[]? CollectedSectors { get; set; }
    public int ConflictingSectors { get; set; }
    public List<int> ConflictingSectorIndices { get; set; } = [];
    [JsonIgnore] public string? SavedReportPath { get; set; }

    public static CaptureReport? FromCommand(string arguments, string? imageOverride = null)
    {
        string[] words = Regex.Matches(arguments, "\"([^\"]*)\"|([^\\s]+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        if (words.Length == 0 || words[0] is not ("read" or "write" or "erase")) return null;
        int fmt = Array.IndexOf(words, "--format");
        string format = fmt >= 0 && fmt + 1 < words.Length ? words[fmt + 1] : "";
        string image = imageOverride ?? (words[0] == "erase" ? "" : words[^1]);
        var report = new CaptureReport { Operation = words[0], Format = format, ImagePath = image, Command = arguments, Layout = DiskLayout.ForFormat(format) };
        var range = Regex.Match(arguments, @"--tracks\s+c=(\d+)-(\d+)(?:\s|$)");
        if (range.Success)
        {
            int start = int.Parse(range.Groups[1].Value), end = int.Parse(range.Groups[2].Value);
            if (start <= end && end <= 255) report.MapCylinders = Enumerable.Range(start, end - start + 1).ToArray();
        }
        return report;
    }

    public bool Feed(string line)
    {
        lock (_gate)
        {
            Log.Add(line);
            if (line.Contains("FATAL ERROR", StringComparison.OrdinalIgnoreCase)) Fatal = true;
            if (Format == "tandy.dmk" && Regex.IsMatch(line, @"\b[1-9]\d* unrecovered errors?\b", RegexOptions.IgnoreCase)) Fatal = true;
            if (line.StartsWith("Cyl-> ")) { _mapTens = line[6..]; return false; }
            if (line.StartsWith("H. S: ") && _mapTens != null)
            {
                string ones = line[6..];
                if (ones.Length == _mapTens.Length && ones.All(char.IsDigit))
                {
                    int tens = 0;
                    MapCylinders = ones.Select((digit, i) =>
                    { if (char.IsDigit(_mapTens[i])) tens = _mapTens[i] - '0'; return tens * 10 + digit - '0'; }).ToArray();
                }
                return false;
            }
            if (line.Trim() == "All tracks verified") { Verified = true; return true; }
            var failed = Regex.Match(line, @"Failed to verify Track\s+(\d+)\.(\d+)", RegexOptions.IgnoreCase);
            if (failed.Success) { GetTrack(int.Parse(failed.Groups[1].Value), int.Parse(failed.Groups[2].Value)).VerifyFailed = true; return true; }
            var map = MapRow.Match(line);
            if (map.Success) { SectorRows[$"{map.Groups[1].Value}.{map.Groups[2].Value}"] = map.Groups[3].Value; return true; }
            var m = Track.Match(line);
            if (!m.Success) return false;
            var t = GetTrack(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            string text = m.Groups[3].Value;
            var counts = Sectors.Match(text);
            if (counts.Success)
            {
                t.Found = int.Parse(counts.Groups[1].Value); t.Expected = int.Parse(counts.Groups[2].Value); t.Attempts++;
                if (t.Found == t.Expected) t.GaveUp = false;
            }
            if (text.Contains("Giving up", StringComparison.OrdinalIgnoreCase)) t.GaveUp = true;
            if (text.Contains("Ignoring unexpected sector", StringComparison.OrdinalIgnoreCase)) t.UnexpectedHeader = true;
            if (text.Contains("Writing Track", StringComparison.OrdinalIgnoreCase)) { t.Written = true; t.VerifyFailed = false; t.Attempts++; }
            return true;
        }
    }

    private TrackCapture GetTrack(int c, int h)
    {
        string key = $"{c}.{h}";
        if (!Tracks.TryGetValue(key, out var value)) Tracks[key] = value = new() { Cylinder = c, Head = h };
        return value;
    }

    public IReadOnlyList<TrackView> SnapshotTracks()
    {
        lock (_gate) return Tracks.Values.OrderBy(t => t.Cylinder).ThenBy(t => t.Head)
            .Select(t => new TrackView(t.Cylinder, t.Head, t.State(Finished.HasValue, Verified), t.Found, t.Expected, t.Attempts)).ToArray();
    }

    public void ContinueFrom(CaptureReport previous)
    {
        if (Operation != "write" || previous.Operation != Operation || previous.Format != Format || previous.ImagePath != ImagePath) return;
        foreach (var pair in previous.Tracks)
        {
            var t = pair.Value;
            Tracks[pair.Key] = new TrackCapture { Cylinder = t.Cylinder, Head = t.Head, Found = t.Found, Expected = t.Expected,
                Attempts = t.Attempts, GaveUp = t.GaveUp, UnexpectedHeader = t.UnexpectedHeader, Written = t.Written, VerifyFailed = t.VerifyFailed };
        }
        PreviousReports = [.. previous.PreviousReports];
        if (previous.SavedReportPath != null) PreviousReports.Add(previous.SavedReportPath);
    }

    public bool[] ValidSectors()
    {
        lock (_gate)
        {
            if (CollectedSectors != null) return (bool[])CollectedSectors.Clone();
            if (Layout == null) return [];
            bool[] valid = new bool[Layout.SectorCount];
            for (int c = 0; c < Layout.Cylinders; c++) for (int h = 0; h < Layout.Heads; h++)
            {
                int offset = Layout.SectorOffset(c, h), count = Layout.Sectors[Layout.TrackIndex(c, h)];
                Tracks.TryGetValue($"{c}.{h}", out var t);
                if (t?.UnexpectedHeader == true) continue;
                for (int s = 0; s < count; s++)
                {
                    int column = MapCylinders == null ? c : Array.IndexOf(MapCylinders, c);
                    valid[offset + s] = SectorRows.TryGetValue($"{h}.{s}", out string? row)
                        ? column >= 0 && column < row.Length && row[column] == '.'
                        : t != null && t.Expected == count && t.Found == count && !t.GaveUp;
                }
            }
            return valid;
        }
    }

    public string Summary()
    {
        var tracks = SnapshotTracks();
        if (Cancelled) return "Cancelled — partial capture retained";
        if (Fatal || ExitCode is not (null or 0)) return "Stopped with an error";
        if (Operation == "write") return Verified ? "Write complete — verified" : "Write complete — not verified";
        if (Operation == "erase") return "Erase complete";
        if (CollectedSectors != null) return $"Collected {CollectedSectors.Count(v => v)}/{CollectedSectors.Length} sectors · {ConflictingSectors} conflicts";
        int found = tracks.Sum(t => t.Found), expected = tracks.Sum(t => t.Expected);
        if (Layout != null && SectorRows.Count > 0) { found = ValidSectors().Count(v => v); expected = Layout.SectorCount; }
        int missing = tracks.Count(t => t.State is TrackState.Missing or TrackState.HeaderMismatch);
        if (found == 0) return Command.Contains("--raw") || Layout == null
            ? "Capture saved — sector completeness unknown"
            : "No sectors decoded — check format, disk and drive setup";
        int retried = tracks.Count(t => t.State == TrackState.Retried);
        return $"{found}/{expected} sectors · {retried} tracks retried · {missing} incomplete tracks";
    }

    public void Finish(int? code, bool cancelled)
    {
        lock (_gate) { ExitCode = code; Cancelled = cancelled; Finished = DateTimeOffset.UtcNow; }
        if (!string.IsNullOrEmpty(ImagePath) && File.Exists(ImagePath))
        {
            using var file = File.OpenRead(ImagePath);
            ImageLength = file.Length; ImageSha256 = Convert.ToHexString(SHA256.HashData(file));
        }
    }

    public string ToText()
    {
        var text = new StringBuilder().AppendLine("HamsterWeazle capture report").AppendLine(Summary())
            .AppendLine($"Started: {Started:O}").AppendLine($"Finished: {Finished:O}")
            .AppendLine($"Image: {ImagePath}").AppendLine($"Format: {Format}").AppendLine($"Command: {Command}")
            .AppendLine($"Operation: {Operation}").AppendLine($"Exit code: {ExitCode}")
            .AppendLine($"Cancelled: {Cancelled}").AppendLine($"Fatal: {Fatal}").AppendLine($"Verified: {Verified}")
            .AppendLine($"SHA-256: {ImageSha256}").AppendLine();
        foreach (string source in Sources) text.AppendLine("Source image: " + source);
        foreach (string previous in PreviousReports) text.AppendLine("Previous attempt report: " + previous);
        foreach (var t in SnapshotTracks()) text.AppendLine($"Track {t.Cylinder}.{t.Head}: {t.State}, {t.Found}/{t.Expected} sectors, {t.Attempts} attempts");
        if (Tracks.Values.Any(t => t.UnexpectedHeader)) text.AppendLine("Unexpected sector headers: possible format, stepping, disk layout or positioning mismatch.");
        var byHead = SnapshotTracks().GroupBy(t => t.Head).ToArray();
        if (byHead.Length > 1 && byHead.Any(g => g.Count() >= 3 && g.All(t => t.Found == 0)) && byHead.Any(g => g.Any(t => t.Found > 0)))
            text.AppendLine("One side decoded no sectors: check single-sided media, format, side selection, cabling and head condition.");
        text.AppendLine().AppendLine("Full tool output:");
        foreach (string line in Log) text.AppendLine(line);
        return text.ToString();
    }

    public static CaptureReport Replay(string log, string format, string operation = "read")
    {
        if (log.StartsWith("HamsterWeazle capture report"))
        {
            string Header(string key) => Regex.Match(log, "^" + Regex.Escape(key) + @": ([^\r\n]*)", RegexOptions.Multiline).Groups[1].Value;
            if (Header("Format") is { Length: > 0 } savedFormat) format = savedFormat;
            if (Header("Operation") is "read" or "write" or "erase") operation = Header("Operation");
            var saved = new CaptureReport { Format = format, Operation = operation, Layout = DiskLayout.ForFormat(format),
                Cancelled = bool.TryParse(Header("Cancelled"), out bool cancelled) && cancelled,
                Fatal = bool.TryParse(Header("Fatal"), out bool fatal) && fatal,
                ExitCode = int.TryParse(Header("Exit code"), out int exit) ? exit : null };
            foreach (string line in log.Replace("\r", "").Split('\n')) saved.Feed(line);
            saved.Finished = DateTimeOffset.UtcNow;
            return saved;
        }
        var report = new CaptureReport { Format = format, Operation = operation, Layout = DiskLayout.ForFormat(format) };
        foreach (string line in log.Replace("\r", "").Split('\n')) report.Feed(line);
        report.Finished = DateTimeOffset.UtcNow;
        return report;
    }
}

public static class CaptureStore
{
    public static string Save(CaptureReport report)
    {
        string basePath = string.IsNullOrEmpty(report.ImagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HamsterWeazle", "diagnostics", "operation")
            : Path.GetFullPath(report.ImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        string stem = basePath + ".capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];
        AtomicCreate(stem + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        AtomicCreate(stem + ".txt", report.ToText());
        report.SavedReportPath = stem + ".json";
        return stem + ".txt";
    }
    private static void AtomicCreate(string path, string text)
    {
        string temp = path + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, overwrite: false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static CaptureReport? FindForImage(string image, bool readOnly = true)
    {
        string full = Path.GetFullPath(image);
        if (!File.Exists(full)) return null;
        using var input = File.OpenRead(full);
        string hash = Convert.ToHexString(SHA256.HashData(input));
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".capture-*.json").OrderDescending())
        {
            try
            {
                var report = JsonSerializer.Deserialize<CaptureReport>(File.ReadAllText(path));
                if (report?.ImageSha256 == hash && (!readOnly || report.Operation == "read") && report.SchemaVersion == 1)
                { report.SavedReportPath = path; return report; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static void FollowRename(string oldImage, string newImage)
    {
        string full = Path.GetFullPath(oldImage);
        using var image = File.OpenRead(newImage);
        string hash = Convert.ToHexString(SHA256.HashData(image));
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".capture-*.json").Order())
        {
            CaptureReport? report;
            try { report = JsonSerializer.Deserialize<CaptureReport>(File.ReadAllText(path)); }
            catch (JsonException) { continue; }
            if (report?.SchemaVersion != 1 || report.ImageSha256 != hash) continue;
            report.ImagePath = Path.GetFullPath(newImage);
            report.PreviousReports.Add(path);
            report.Log.Add("[image renamed from " + Path.GetFileName(oldImage) + "]");
            Save(report);
        }
    }
}

public sealed record FormatProbeResult(int Score, int CompleteTracks, int IncompleteTracks, bool Strong)
{
    public static FormatProbeResult Evaluate(string log, string format, int exitCode)
    {
        var report = CaptureReport.Replay(log, format);
        var tracks = report.SnapshotTracks();
        int expected = tracks.Sum(t => t.Expected), found = tracks.Sum(t => Math.Min(t.Found, t.Expected));
        int complete = tracks.Count(t => t.Expected > 0 && t.Found == t.Expected && t.State != TrackState.HeaderMismatch);
        int incomplete = tracks.Count - complete;
        bool valid = exitCode == 0 && !report.Fatal && found > 0 && expected > 0 && tracks.All(t => t.State != TrackState.HeaderMismatch);
        int target = (report.Layout?.Heads ?? 2) * 2;
        bool coversProbe = Enumerable.Range(0, 2).All(c => Enumerable.Range(0, report.Layout?.Heads ?? 2)
            .All(h => tracks.Any(t => t.Cylinder == c && t.Head == h && t.Found == t.Expected && t.Expected > 0)));
        return new(valid ? Math.Max(1, found * 100 / expected) : -1, complete, incomplete,
            valid && complete >= target && incomplete == 0 && coversProbe);
    }
}
