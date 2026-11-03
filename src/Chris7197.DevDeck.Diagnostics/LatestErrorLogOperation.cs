using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Diagnostics;

public sealed class LatestErrorLogOperation(LatestErrorLogService latestLog, DevDeckStatusProviders statuses) : IDevDeckOperation, IDevDeckLatestLogCommands, IDevDeckCliCommandSource
{
    public string? FindLatest(string solutionPath) => latestLog.FindLatest(solutionPath);
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--find-latest-log", "--find-latest-log [solution.sln]", FindLatestCliAsync),
        new("--open-latest-log", "--open-latest-log [solution.sln]", OpenLatestCliAsync)
    ];

    private async Task<int> OpenLatestCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--open-latest-log [solution.sln]");
        DeveloperContext? context = await cli.ResolveContextAsync(
            cli.Arguments.Count == 2 ? cli.Arguments[1] : null, Name);
        return context is null ? 0 : await cli.RunOperationAsync(this, context);
    }

    private async Task<int> FindLatestCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--find-latest-log [solution.sln]");
        DeveloperContext? context = await cli.ResolveContextAsync(
            cli.Arguments.Count == 2 ? cli.Arguments[1] : null, "Latest Log");
        if (context is null) return 0;
        cli.Output.WriteLine("Solution: " + context.RequireSolutionPath());
        cli.Output.WriteLine("Latest log: " + (FindLatest(context.RequireSolutionPath()) ?? "Not Found"));
        return 0;
    }

    public string Name => "Latest\nLog";
    public string PluginActionId => "com.chris7197.devdeck.latesterrorlog";
    public string? IconName => DevDeckIcons.LatestLog;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? settings, CancellationToken cancellationToken = default) =>
        context is null ? Task.FromResult(DevDeckStatusService.MissingSolution(this)) :
        Task.FromResult(statuses.LogState(context));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string solutionPath = context.Developer.RequireSolutionPath();
        await context.ProgressAsync("Finding\nLog...");

        string? path = latestLog.OpenLatest(solutionPath);
        if (path is null)
        {
            await context.ProgressAsync("Not\nFound");
            return new DevDeckOperationResult(false, "No matching log found for solution: " + solutionPath);
        }

        await context.ProgressAsync("Log\nOpened");
        return new DevDeckOperationResult(true, $"Solution: {solutionPath}{Environment.NewLine}Latest log: {path}");
    }
}
