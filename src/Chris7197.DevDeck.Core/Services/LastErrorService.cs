using System.Text;
using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core;

public sealed record LastErrorInfo(string LogPath, string Summary, string Details, string? SourcePath, int? SourceLine);

/// <summary>
/// Extracts the most recent error from plain-text logs and CSV logs (including multiline quoted fields).
/// Reads at most the tail of the file, even when a log is very large. Does not mutate the log.
/// </summary>
public sealed class LastErrorService(LatestErrorLogService latestLogs, DeckSettings settings)
{
    private static readonly Regex ErrorLine = new(@"(?i)(?:\b(?:ERROR|FATAL|CRITICAL|UNHANDLED)\b|\b[A-Za-z0-9_.]*Exception\b)", RegexOptions.Compiled);
    private static readonly Regex StackLocation = new(@"\bin\s+(?<file>.+?):line\s+(?<line>\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MsBuildLocation = new(@"(?<file>(?:[A-Za-z]:[\\/]|\.{1,2}[\\/])[^\r\n()]+\.(?:cs|vb))\((?<line>\d+)(?:,\d+)?\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LastErrorInfo? FindLast(string solutionPath)
    {
        IReadOnlyList<string> logs = latestLogs.FindRecent(solutionPath, settings.LastErrorSearchFileCount);
        if (logs.Count == 0) return null;

        foreach (string logPath in logs)
        {
            string contents = ReadTail(logPath, settings.LastErrorMaxReadBytes);
            string? details = Path.GetExtension(logPath).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? ReadLastCsvError(logPath, contents)
                : null;
            details ??= ReadLastTextError(contents);
            if (details is null) continue;

            (string? file, int? line) = FindSourceLocation(details, Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);
            string summary = details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "Error";
            if (summary.Length > 180) summary = summary[..180] + "...";
            return new LastErrorInfo(logPath, summary, details, file, line);
        }
        return new LastErrorInfo(logs[0], "No exception/error entry identified", "", null, null);
    }

    public static (string? File, int? Line) FindSourceLocation(string errorText, string solutionDirectory)
    {
        var locations = new List<(string Path, int Line)>();
        foreach (string line in errorText.Split('\n'))
        {
            foreach (Regex regex in new[] { StackLocation, MsBuildLocation })
            {
                foreach (Match match in regex.Matches(line))
                {
                    if (!int.TryParse(match.Groups["line"].Value, out int lineNumber) || lineNumber <= 0) continue;
                    string candidate = match.Groups["file"].Value.Trim().Trim('"');
                    if (!candidate.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                        !candidate.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        string full = Path.GetFullPath(Path.IsPathRooted(candidate)
                            ? candidate : Path.Combine(solutionDirectory, candidate));
                        if (File.Exists(full)) locations.Add((full, lineNumber));
                    }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
                }
            }
        }

        // Prefer the user's source tree over framework/dependency stacks when several locations exist.
        string prefix = Path.GetFullPath(solutionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var preferred = locations.FirstOrDefault(x => x.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (preferred.Path is not null) return (preferred.Path, preferred.Line);
        return locations.Count > 0 ? (locations[0].Path, locations[0].Line) : (null, null);
    }

    private static string ReadTail(string file, int maxBytes)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool clipped = fs.Length > maxBytes;
        if (clipped) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8, true);
        if (clipped) reader.ReadLine(); // Drop possibly partial first line.
        return reader.ReadToEnd();
    }

    private static string? ReadLastTextError(string text)
    {
        string[] lines = text.Split('\n');
        int last = -1;
        for (int i = 0; i < lines.Length; i++)
            if (ErrorLine.IsMatch(lines[i])) last = i;
        if (last < 0) return null;
        // Include stack-trace lines following the error, without accidentally returning a huge dump.
        return string.Join('\n', lines.Skip(last).Take(70)).Trim();
    }

    private static string? ReadLastCsvError(string logPath, string contents)
    {
        string? header;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs)) header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header)) return null;
        char delimiter = header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
        string[] headers = CsvRecords(header, delimiter).FirstOrDefault() ?? [];
        int level = FindColumn(headers, "level", "severity", "loglevel", "type");
        int message = FindColumn(headers, "message", "errormessage", "error", "description", "details", "text");
        int exception = FindColumn(headers, "exception", "exceptiondetail", "exceptiondetails", "exceptionmessage");
        int stack = FindColumn(headers, "stacktrace", "stack", "callstack", "exceptionstacktrace");
        if (level < 0 && message < 0 && exception < 0 && stack < 0) return null;

        string? latest = null;
        foreach (string[] row in CsvRecords(contents, delimiter))
        {
            string value(int index) => index >= 0 && index < row.Length ? row[index].Trim() : "";
            bool error = Regex.IsMatch(value(level), @"(?i)^(error|fatal|critical)$") ||
                         !string.IsNullOrWhiteSpace(value(exception)) ||
                         ErrorLine.IsMatch(value(message));
            if (!error) continue;
            string detail = string.Join(Environment.NewLine, new[] { value(message), value(exception), value(stack) }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
            if (!string.IsNullOrWhiteSpace(detail)) latest = detail;
        }
        return latest;
    }

    private static int FindColumn(string[] headers, params string[] names) =>
        Array.FindIndex(headers, h => names.Contains(new string(h.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()));

    private static IEnumerable<string[]> CsvRecords(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == delimiter && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else if ((ch == '\n' || ch == '\r') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString()); field.Clear();
                if (fields.Any(x => x.Length > 0)) yield return fields.ToArray();
                fields.Clear();
            }
            else field.Append(ch);
        }
        fields.Add(field.ToString());
        if (fields.Any(x => x.Length > 0)) yield return fields.ToArray();
    }
}
