using System.Diagnostics;

using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Diagnostics;

public sealed class LastErrorOperation(LastErrorService errors, VisualStudioSourceNavigator navigator, DeckLog log, DevDeckStatusProviders statuses) : IDevDeckOperation, IDevDeckLastErrorCommands, IDevDeckCliCommandSource
{
    public LastErrorInfo? FindLast(string solutionPath) => errors.FindLast(solutionPath);
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
    [
        new("--find-last-error", "--find-last-error [solution.sln]", FindLastCliAsync),
        new("--last-error", "--last-error [solution.sln]", OpenLastCliAsync)
    ];

    private async Task<int> OpenLastCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--last-error [solution.sln]");
        DeveloperContext? context = await cli.ResolveContextAsync(
            cli.Arguments.Count == 2 ? cli.Arguments[1] : null, Name);
        return context is null ? 0 : await cli.RunOperationAsync(this, context);
    }

    private async Task<int> FindLastCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--find-last-error [solution.sln]");
        DeveloperContext? context = await cli.ResolveContextAsync(
            cli.Arguments.Count == 2 ? cli.Arguments[1] : null, "Last Error");
        if (context is null) return 0;
        LastErrorInfo? result = FindLast(context.RequireSolutionPath());
        if (result is null) { cli.Output.WriteLine("No log file found."); return 1; }
        cli.Output.WriteLine("Log: " + result.LogPath);
        cli.Output.WriteLine("Error: " + result.Summary);
        cli.Output.WriteLine("Source: " + (result.SourcePath ?? "(not identified)"));
        cli.Output.WriteLine("Line: " + (result.SourceLine?.ToString() ?? "(not identified)"));
        cli.Output.WriteLine("Details:\n" + result.Details);
        return string.IsNullOrWhiteSpace(result.Details) ? 1 : 0;
    }

    public string Name => "Last\nError";
    public string PluginActionId => "com.chris7197.devdeck.lasterror";
    public string? IconName => DevDeckIcons.LastError;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? settings, CancellationToken cancellationToken = default) =>
        context is null ? Task.FromResult(DevDeckStatusService.MissingSolution(this)) :
        Task.FromResult(statuses.LastErrorState(context));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string solution = context.Developer.RequireSolutionPath();
        await context.ProgressAsync("Finding\nError...");
        LastErrorInfo? result = errors.FindLast(solution);
        if (result is null) return new DevDeckOperationResult(false, "No log file found for: " + solution);
        if (string.IsNullOrWhiteSpace(result.Details))
        {
            OpenFile(result.LogPath);
            return new DevDeckOperationResult(false, "No recognizable error entry; opened log instead: " + result.LogPath);
        }

        if (result.SourcePath is not null && result.SourceLine is not null)
        {
            try
            {
                await navigator.OpenAtLineAndOffsetAsync(solution, result.SourcePath, result.SourceLine.Value, Consts.OpenAtOffsetDefault);
                await context.ProgressAsync("Source\nOpened");
                return new DevDeckOperationResult(true, $"{result.Summary}{Environment.NewLine}{result.SourcePath}:{result.SourceLine}");
            }
            catch (Exception ex)
            {
                log.Write("Source navigation unavailable; opening log instead: " + ex);
            }
        }

        OpenFile(result.LogPath);
        await context.ProgressAsync("Error\nLog");
        return new DevDeckOperationResult(true, $"{result.Summary}{Environment.NewLine}" +
            "No accessible source-line location (or Visual Studio navigation unavailable); opened log: " + result.LogPath);
    }

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
