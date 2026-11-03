namespace chris7197.DevDeck.Core;

public sealed class GitSyncOperation(GitSyncService sync, DeckLog log, bool saveAllBeforeCommit) : IDevDeckOperation
{
    public string Name => "Sync\nMain";
    public string PluginActionId => "com.chris7197.devdeck.syncmain";
    public string? IconName => DevDeckIcons.SyncMain;
    public IReadOnlyList<string> CliCommands => ["--repo", "--active-vs"];
    public string CliUsage => "--repo <path> | --active-vs true";
    public bool SaveAllBeforeExecute => saveAllBeforeCommit;

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        if (args.Count != 2) return false;
        if (args[0] == "--repo")
        {
            target = args[1];
            return !string.IsNullOrWhiteSpace(target);
        }
        return args[0] == "--active-vs" && bool.TryParse(args[1], out bool enabled) && enabled;
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        SyncResult result = await sync.ExecuteAsync(
            context.Developer.WorkingDirectory,
            context.ProgressAsync,
            context.CancellationToken);

        await context.ProgressAsync(result.Success ? "Merged!" : result.HasConflicts ? "Merge\nCONFLICT" : "Failed\nsee log");
        return new DevDeckOperationResult(result.Success, result.Message + Environment.NewLine + "Log: " + log.FilePath, result.HasConflicts);
    }

    public string ConfirmationText(DeveloperContext context)
    {
        return $"Execute Sync Main in {context.WorkingDirectory}?\n\n" +
            "This will stage and commit changes, then fetch and merge.";
    }
}
