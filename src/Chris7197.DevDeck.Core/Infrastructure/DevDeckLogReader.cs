using System.Text;
using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core;

public enum DevDeckLogSeverity { Information, Warning, Error }
public sealed record DevDeckLogEntry(DateTimeOffset Timestamp, DevDeckLogSeverity Severity,
    string Summary, string Details);
public sealed record DevDeckLogPoll(IReadOnlyList<DevDeckLogEntry> Entries, bool Reinitialized);

/// <summary>
/// Incremental reader for the shared DevDeck logfile. Starts at EOF for alerts while still
/// providing existing entries to the viewer. All file reads allow the writer to keep working.
/// </summary>
public sealed class DevDeckLogReader(string path, int maxEntries = 300, int maxReadBytes = 524_288)
{
    private static readonly Regex Header = new(
        @"^\[(?<when>\d{4}-\d\d-\d\dT[^\]]+)\]\s*(?:\[(?<level>INFO|WARN|WARNING|ERROR)\]\s*)?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private readonly string _path = Path.GetFullPath(path);
    private readonly int _maxEntries = Math.Max(1, maxEntries);
    private readonly int _maxReadBytes = Math.Max(4096, maxReadBytes);
    private long _offset;
    private DateTime _createdUtc;
    private string _partialLine = "";
    private bool _initialized;

    public IReadOnlyList<DevDeckLogEntry> Initialize()
    {
        try
        {
            using var file = Open();
            if (file is null)
            {
                Reset();
                _initialized = true;
                return [];
            }
            _createdUtc = File.GetCreationTimeUtc(_path);
            long length = file.Length;
            long from = Math.Max(0, length - _maxReadBytes);
            file.Position = from;
            string text = ReadText(file, (int)(length - from));
            if (from > 0)
            {
                int firstNewline = text.IndexOf('\n');
                text = firstNewline >= 0 ? text[(firstNewline + 1)..] : "";
            }
            _offset = length;
            _initialized = true;
            _partialLine = "";
            return Parse(text, _maxEntries);
        }
        catch (IOException) { Reset(); _initialized = true; return []; }
        catch (UnauthorizedAccessException) { Reset(); _initialized = true; return []; }
    }

    public DevDeckLogPoll Poll()
    {
        if (!_initialized) return new(Initialize(), true);
        try
        {
            using var file = Open();
            if (file is null)
            {
                bool wasPresent = _offset != 0;
                Reset();
                return new([], wasPresent);
            }
            DateTime created = File.GetCreationTimeUtc(_path);
            long length = file.Length;
            if (length < _offset || (_createdUtc != default && created != _createdUtc))
                return new(Initialize(), true);
            _createdUtc = created;
            if (length == _offset) return new([], false);
            long available = length - _offset;
            long from = _offset;
            if (available > _maxReadBytes)
                from = length - _maxReadBytes; // bounded memory even after a large write
            file.Position = from;
            string delta = ReadText(file, (int)(length - from));
            _offset = length;
            if (from > 0 && from != _offset - available)
            {
                _partialLine = "";
                int firstNewline = delta.IndexOf('\n');
                delta = firstNewline < 0 ? "" : delta[(firstNewline + 1)..];
            }
            string joined = _partialLine + delta;
            int lastNewline = joined.LastIndexOf('\n');
            if (lastNewline < 0)
            {
                _partialLine = joined.Length <= 65536 ? joined : "";
                return new([], false);
            }
            _partialLine = joined[(lastNewline + 1)..];
            string complete = joined[..(lastNewline + 1)];
            return new(Parse(complete, _maxEntries), false);
        }
        catch (IOException) { return new([], false); } // writer sharing / temporary antivirus locks
        catch (UnauthorizedAccessException) { return new([], false); }
    }

    private void Reset() { _offset = 0; _createdUtc = default; _partialLine = ""; }

    private FileStream? Open() => File.Exists(_path)
        ? new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
        : null;

    private static string ReadText(FileStream file, int count)
    {
        byte[] bytes = new byte[count];
        int got = 0;
        while (got < count)
        {
            int read = file.Read(bytes, got, count - got);
            if (read == 0) break;
            got += read;
        }
        return Encoding.UTF8.GetString(bytes, 0, got);
    }

    public static IReadOnlyList<DevDeckLogEntry> Parse(string content, int maxEntries = 300)
    {
        var items = new List<DevDeckLogEntry>();
        DateTimeOffset currentTime = default;
        DevDeckLogSeverity currentSeverity = default;
        var details = new StringBuilder();
        string currentSummary = "";
        bool hasHeader = false;
        void Flush()
        {
            if (!hasHeader) return;
            items.Add(new(currentTime, currentSeverity, currentSummary, details.ToString().TrimEnd()));
            if (items.Count > maxEntries) items.RemoveAt(0);
        }
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            Match match = Header.Match(line);
            if (match.Success && DateTimeOffset.TryParse(match.Groups["when"].Value, out var time))
            {
                Flush();
                currentTime = time;
                currentSummary = match.Groups["message"].Value;
                currentSeverity = Classify(match.Groups["level"].Value, currentSummary);
                details.Clear();
                details.Append(currentSummary);
                hasHeader = true;
            }
            else if (hasHeader)
            {
                if (details.Length < 65536) details.AppendLine().Append(line);
            }
        }
        Flush();
        return items;
    }

    public static DevDeckLogSeverity Classify(string level, string message)
    {
        if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) return DevDeckLogSeverity.Error;
        if (level.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
            level.Equals("WARNING", StringComparison.OrdinalIgnoreCase)) return DevDeckLogSeverity.Warning;
        if (level.Equals("INFO", StringComparison.OrdinalIgnoreCase)) return DevDeckLogSeverity.Information;

        // Compatibility with logs written before severity markers existed.
        string[] oldErrors = ["Action failed", "SYNC FAILED:", "Plugin startup/connection failure:",
            "Status refresh cycle failed", "Simulator action failed:"];
        if (oldErrors.Any(s => message.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            return DevDeckLogSeverity.Error;
        return DevDeckLogSeverity.Information;
    }
}
