using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Read-only key statuses. Never fetches, commits, modifies files or runs migrations.
/// Extend this service with more live status providers as new DevDeck actions are added.
/// </summary>
public sealed class DevDeckStatusService(DeckSettings settings, GitProcess git,
    LatestErrorLogService latestLogs, LastErrorService errors, MigrationProjectResolver migrations)
{
    private sealed record ErrorCacheEntry(string? LatestPath, long WriteTicks, LastErrorInfo? Error);
    private readonly Dictionary<string, ErrorCacheEntry> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _errorSync = new();

    public async Task<DevDeckButtonState> GetStateAsync(IDevDeckOperation action,
        DeveloperContext? context, CancellationToken cancellationToken = default)
    {
        if (context is null && action.RequiresSolutionContext)
            return new(action.Name, "Focus VS", DeckStatus.Unavailable,
                "Focus Visual Studio once, or select a solution in the simulator.", IconName: action.IconName);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action.PluginActionId switch
            {
                "com.chris7197.devdeck.syncmain" => await GitStateAsync(context!, cancellationToken),
                "com.chris7197.devdeck.latesterrorlog" => LogState(context!),
                "com.chris7197.devdeck.lasterror" => LastErrorState(context!),
                "com.chris7197.devdeck.newmigration" => MigrationState(context!),
                _ => await action.GetStateAsync(context, cancellationToken)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(action.Name, "Unavailable", DeckStatus.Unavailable, ex.Message, IconName: action.IconName);
        }
    }

    public async Task<DevDeckButtonState> GetConfiguredStateAsync(IDevDeckOperation action,
        DeveloperContext? context, DeckActionSettings? instanceSettings, CancellationToken cancellationToken = default)
    {
        if (action is TeamsChannelOperation or OpenSolutionOperation or OpenInVsCodeOperation or DbFlightRecorderOperation)
        {
            try { return await action.GetConfiguredStateAsync(context, instanceSettings, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) { return new(action.Name, "Unavailable", DeckStatus.Unavailable, e.Message, IconName: action.IconName); }
        }
        return await GetStateAsync(action, context, cancellationToken);
    }

    private async Task<DevDeckButtonState> GitStateAsync(DeveloperContext context, CancellationToken ct)
    {
        string work = context.WorkingDirectory;
        GitOutput root = await git.RunAsync(work, ct, "rev-parse", "--show-toplevel");
        if (!root.Ok) return new("Sync Main", "No Git repo", DeckStatus.Unavailable, root.Error, IconName: DevDeckIcons.SyncMain);
        work = root.StdOut.Trim();

        GitOutput branch = await git.RunAsync(work, ct, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!branch.Ok) return new("Sync Main", "Detached", DeckStatus.Warning, "Checkout a feature branch first.", IconName: DevDeckIcons.SyncMain);
        string branchName = branch.StdOut.Trim();
        if (string.Equals(branchName, settings.MainBranch, StringComparison.OrdinalIgnoreCase))
            return new("Sync Main", "On main", DeckStatus.Warning, "Select a feature branch before merging main.", IconName: DevDeckIcons.SyncMain);

        // Must not fetch in a background loop. Counts reflect the LOCAL cached tracking ref.
        string upstream = $"refs/remotes/{settings.Remote}/{settings.MainBranch}";
        GitOutput compare = await git.RunAsync(work, ct, "rev-list", "--left-right", "--count", $"HEAD...{upstream}");
        if (!compare.Ok)
            return new("Sync Main", "Fetch needed", DeckStatus.Warning,
                $"No local {settings.Remote}/{settings.MainBranch} tracking ref (or no shared history). " +
                "Background status never accesses the network.", IconName: DevDeckIcons.SyncMain);
        string[] counts = compare.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2 || !int.TryParse(counts[0], out int ahead) || !int.TryParse(counts[1], out int behind))
            return new("Sync Main", "Git unknown", DeckStatus.Unavailable, compare.StdOut, IconName: DevDeckIcons.SyncMain);
        string subtitle = behind == 0 ? "Up to date" : $"↓ {behind} behind";
        string detail = $"{branchName}\nAhead: {ahead} / Behind: {behind} vs cached {settings.Remote}/{settings.MainBranch}. " +
            "Actual remote may have newer commits.";
        return new("Sync Main", subtitle, behind > 0 ? DeckStatus.Warning : DeckStatus.Success, detail, IconName: DevDeckIcons.SyncMain);
    }

    private DevDeckButtonState LogState(DeveloperContext context)
    {
        string? path = latestLogs.FindLatest(context.RequireSolutionPath());
        if (path is null) return new("Latest Log", "No files", DeckStatus.Unavailable, IconName: DevDeckIcons.LatestLog);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        return new("Latest Log", Age(modified), DeckStatus.Success,
            $"{Path.GetFileName(path)}\nLast written: {modified.ToLocalTime():G}\n{path}", IconName: DevDeckIcons.LatestLog);
    }

    private DevDeckButtonState LastErrorState(DeveloperContext context)
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

    private DevDeckButtonState MigrationState(DeveloperContext context)
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
