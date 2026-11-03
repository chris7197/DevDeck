using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Git;

public sealed class GitSyncOperation(GitSyncService sync, DeckLog log, bool saveAllBeforeCommit, GitStatusProvider statuses, string commitMessage) : IDevDeckOperation, IDevDeckCliCommandSource
{
    public string Name => "Sync\nMain";
    public string PluginActionId => "com.chris7197.devdeck.syncmain";
    public DevDeckExecutionKind ExecutionKind => DevDeckExecutionKind.ProjectMutation;
    public string? IconName => DevDeckIcons.SyncMain;
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--repo", "--repo <path>", RunCliAsync),
        new("--active-vs", "--active-vs true", RunCliAsync)
    ];

    private async Task<int> RunCliAsync(DevDeckCliContext cli)
    {
        if (!TryParseCliArguments(cli.Arguments, out string? target))
            return await cli.InvalidArgumentsAsync("--repo <path> | --active-vs true");
        cli.Output.WriteLine("WARNING: This stages and commits ALL repo changes, then fetches and merges without confirmation.");
        DeveloperContext? developer = await cli.ResolveContextAsync(target, Name, SaveAllBeforeExecute);
        if (developer is null) { cli.Output.WriteLine("Cancelled."); return 0; }
        return await cli.RunOperationAsync(this, developer);
    }

    public bool SaveAllBeforeExecute => saveAllBeforeCommit;
    public string ConfirmationText(DeveloperContext context) =>
        $"Execute Sync Main in {context.WorkingDirectory}?\n\n" +
        "This will STAGE AND COMMIT ALL changes as '" + commitMessage + "', then fetch and merge. " +
        "Save unsaved Visual Studio files first.\n\nContinue?";

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? settings, CancellationToken cancellationToken = default) =>
        context is null ? Task.FromResult(DevDeckStatusService.MissingSolution(this)) :
        statuses.GetStateAsync(context, cancellationToken);

    private bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
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
}
