using System.Diagnostics;

namespace chris7197.DevDeck.Core;

public sealed class LastErrorOperation(LastErrorService errors, VisualStudioSourceNavigator navigator, DeckLog log) : IDevDeckOperation
{
    public string Name => "Last\nError";
    public string PluginActionId => "com.chris7197.devdeck.lasterror";
    public string? IconName => DevDeckIcons.LastError;
    public IReadOnlyList<string> CliCommands => ["--last-error"];
    public string CliUsage => "--last-error [solution.sln]";

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = args.Count == 2 ? args[1] : null;
        return (args.Count is 1 or 2) && args[0] == "--last-error";
    }

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
