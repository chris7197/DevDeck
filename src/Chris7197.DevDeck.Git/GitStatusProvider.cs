using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Git;

/// <summary>Read-only status checks; no fetch/merge on the background refresh loop.</summary>
public sealed class GitStatusProvider(DeckSettings settings, GitProcess git)
{
    public async Task<DevDeckButtonState> GetStateAsync(DeveloperContext context, CancellationToken ct)
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
}
