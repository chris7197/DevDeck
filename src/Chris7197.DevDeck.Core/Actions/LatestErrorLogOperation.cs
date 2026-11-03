namespace chris7197.DevDeck.Core;

public sealed class LatestErrorLogOperation(LatestErrorLogService latestLog) : IDevDeckOperation
{
    public string Name => "Latest\nLog";
    public string PluginActionId => "com.chris7197.devdeck.latesterrorlog";
    public string? IconName => DevDeckIcons.LatestLog;
    public IReadOnlyList<string> CliCommands => ["--open-latest-log"];
    public string CliUsage => "--open-latest-log [solution.sln]";

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = args.Count == 2 ? args[1] : null;
        return (args.Count is 1 or 2) && args[0] == "--open-latest-log";
    }

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
