namespace chris7197.DevDeck.Core;

/// <summary>Domain-specific, read-only status queries reused by action-owned status handlers.</summary>
public sealed class DevDeckStatusProviders(LatestErrorLogService latestLogs, LastErrorService errors, MigrationProjectResolver migrations)
{
    private sealed record ErrorCacheEntry(string? LatestPath, long WriteTicks, LastErrorInfo? Error);
    private readonly Dictionary<string, ErrorCacheEntry> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _errorSync = new();

    public DevDeckButtonState LogState(DeveloperContext context)
    {
        string? path = latestLogs.FindLatest(context.RequireSolutionPath());
        if (path is null) return new("Latest Log", "No files", DeckStatus.Unavailable, IconName: DevDeckIcons.LatestLog);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        return new("Latest Log", Age(modified), DeckStatus.Success,
            $"{Path.GetFileName(path)}\nLast written: {modified.ToLocalTime():G}\n{path}", IconName: DevDeckIcons.LatestLog);
    }

    public DevDeckButtonState LastErrorState(DeveloperContext context)
    {
        string solution = context.RequireSolutionPath();
        // Avoid re-reading a multi-megabyte log on every poll when nothing has changed.
        string? latestPath = latestLogs.FindLatest(solution);
        if (latestPath is null) return new("Last Error", "No logs", DeckStatus.Unavailable, IconName: DevDeckIcons.LastError);
        long ticks = File.GetLastWriteTimeUtc(latestPath).Ticks;
        ErrorCacheEntry? cached;
        lock (_errorSync) _errors.TryGetValue(solution, out cached);
        LastErrorInfo? last;
        if (cached is not null && cached.LatestPath == latestPath && cached.WriteTicks == ticks)
            last = cached.Error;
        else
        {
            last = errors.FindLast(solution);
            lock (_errorSync) _errors[solution] = new ErrorCacheEntry(latestPath, ticks, last);
        }
        if (last is null || string.IsNullOrWhiteSpace(last.Details))
            return new("Last Error", "None found", DeckStatus.Success, "No recognised error in recent logs.", IconName: DevDeckIcons.LastError);
        string summary = last.Summary.Replace('\r', ' ').Replace('\n', ' ');
        return new("Last Error", "Error found", DeckStatus.Error,
            $"{summary}\n{last.LogPath}" + (last.SourcePath is null ? "" : $"\n{last.SourcePath}:{last.SourceLine}"), IconName: DevDeckIcons.LastError);
    }

    public DevDeckButtonState MigrationState(DeveloperContext context)
    {
        MigrationProject project = migrations.Resolve(context.RequireSolutionPath());
        return new("New Migration", "Ready", DeckStatus.Success,
            $"Target: {Path.GetFileNameWithoutExtension(project.ProjectFile)}\n{project.ProjectFile}", IconName: DevDeckIcons.NewMigration);
    }

    private static string Age(DateTime utc)
    {
        double seconds = Math.Max(0, (DateTime.UtcNow - utc).TotalSeconds);
        return seconds < 60 ? $"{(int)seconds}s ago" : seconds < 3600 ? $"{(int)(seconds / 60)}m ago" :
            seconds < 86400 ? $"{(int)(seconds / 3600)}h ago" : $"{(int)(seconds / 86400)}d ago";
    }
}
