using System.Diagnostics;
using System.Text;

namespace chris7197.DevDeck.Core;

/// <summary>Private local output for user-invoked investigations. Never syncs or uploads anything.</summary>
public static class DeveloperReportStore
{
    private static readonly object RetentionSync = new();
    private static ReportRetentionService _retention = new();
    private static long _lastPruneTicks;

    public static string ReportsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "chris7197", "DevDeck", "Reports");

    public static void ConfigureRetention(int days, int maxPerKind) =>
        Volatile.Write(ref _retention, new ReportRetentionService(days, maxPerKind));

    /// <summary>Best effort. Cleanup failures never prevent new captures.</summary>
    public static void PruneReports(Action<Exception>? onError = null)
    {
        try { Volatile.Read(ref _retention).Prune(ReportsRoot, DateTime.UtcNow, onError); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { onError?.Invoke(e); }
    }

    private static void MaybePruneReports()
    {
        long now = DateTime.UtcNow.Ticks;
        lock (RetentionSync)
        {
            if (now - _lastPruneTicks < TimeSpan.FromHours(1).Ticks) return;
            _lastPruneTicks = now;
        }
        PruneReports();
    }

    public static string CreateDirectory(string kind, string? label = null)
    {
        if (kind is not ("DbFlights" or "Investigations"))
            throw new ArgumentException("Unsupported report category.", nameof(kind));
        MaybePruneReports();
        string root = Path.Combine(ReportsRoot, kind);
        Directory.CreateDirectory(root);
        string name = string.Concat((label ?? "Capture").Where(c => char.IsLetterOrDigit(c) || c == '-'));
        if (name.Length > 35) name = name[..35];
        string path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Open(string file)
    {
        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    public static string Limited(string? text, int chars = 4000)
    {
        string safe = text ?? "";
        return safe.Length <= chars ? safe : safe[..chars] + "\n[TRUNCATED]";
    }
}
