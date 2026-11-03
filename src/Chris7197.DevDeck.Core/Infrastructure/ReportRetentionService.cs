using System.Globalization;
using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Prunes only DevDeck timestamp/GUID-named capture directories. Recent captures
/// are never deleted, so a running SQL capture or investigation isn't affected.
/// </summary>
public sealed class ReportRetentionService(int retentionDays = 30, int maxCapturesPerKind = 200)
{
    private static readonly Regex CaptureName = new(
        @"^(?<time>\d{8}-\d{6})-[\p{L}\p{N}-]{0,35}-[a-fA-F0-9]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public int RetentionDays { get; } = retentionDays is >= 1 and <= 3650
        ? retentionDays : throw new ArgumentOutOfRangeException(nameof(retentionDays));
    public int MaxCapturesPerKind { get; } = maxCapturesPerKind is >= 5 and <= 10000
        ? maxCapturesPerKind : throw new ArgumentOutOfRangeException(nameof(maxCapturesPerKind));

    public int Prune(string reportRoot, DateTime utcNow, Action<Exception>? onError = null)
    {
        if (!Directory.Exists(reportRoot)) return 0;
        if ((File.GetAttributes(reportRoot) & FileAttributes.ReparsePoint) != 0) return 0;
        int deleted = 0;
        foreach (string kind in new[] { "Investigations", "DbFlights" })
        {
            string directory = Path.Combine(reportRoot, kind);
            if (!Directory.Exists(directory)) continue;
            try
            {
                // Do not traverse a symlink/junction substituted for the report category.
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var entries = new List<(string Path, DateTime Captured)>();
                foreach (string candidate in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                        var match = CaptureName.Match(Path.GetFileName(candidate));
                        if (!match.Success || !DateTime.TryParseExact(match.Groups["time"].Value,
                            "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                        // Timestamp in capture folder is local time. Use filesystem UTC time
                        // to decide actual age without assumptions about DST transitions.
                        DateTime createdUtc = Directory.GetCreationTimeUtc(candidate);
                        if (createdUtc > utcNow) createdUtc = utcNow;
                        entries.Add((candidate, createdUtc));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { onError?.Invoke(e); }
                }
                var ordered = entries.OrderByDescending(x => x.Captured).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var capture = ordered[i];
                    TimeSpan age = utcNow - capture.Captured;
                    // Protect captures that might still be getting populated.
                    if (age < TimeSpan.FromHours(2)) continue;
                    if (age.TotalDays <= RetentionDays && i < MaxCapturesPerKind) continue;
                    try
                    {
                        // We never follow/erase nested reparse points as part of cleanup.
                        if (ContainsReparsePoint(capture.Path)) continue;
                        Directory.Delete(capture.Path, recursive: true);
                        deleted++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { onError?.Invoke(e); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { onError?.Invoke(e); }
        }
        return deleted;
    }

    private static bool ContainsReparsePoint(string path)
    {
        var pending = new Stack<string>();
        pending.Push(path);
        int visited = 0;
        while (pending.Count > 0)
        {
            if (++visited > 10000) return true; // Fail closed on unexpectedly huge trees.
            string folder = pending.Pop();
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
            }
        }
        return false;
    }
}
