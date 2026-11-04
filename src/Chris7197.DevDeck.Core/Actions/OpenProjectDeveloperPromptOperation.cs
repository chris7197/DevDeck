using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>Open a VS Developer Command Prompt in the active document's containing project.</summary>
public sealed class OpenProjectDeveloperPromptOperation(VisualStudioComDispatcher dispatcher) : IDevDeckOperation
{
    public const string ActionId = "com.chris7197.devdeck.projectdevcmd";
    private readonly ProjectDevCmdResolver _resolver = new(dispatcher);

    public string Name => "Project Developer Prompt";
    public string PluginActionId => ActionId;
    public string? IconName => "project-devcmd";
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => true;

    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target)
    {
        target = null;
        return false;
    }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context,
        CancellationToken cancellationToken = default) => Task.FromResult(new DevDeckButtonState(
            "Dev Cmd", context?.SolutionName ?? "Focus VS",
            context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Open a Developer Command Prompt in the project containing the active Visual Studio file.",
            IconName: IconName));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        uint pid = context.Developer.VisualStudioProcessId ?? throw new InvalidOperationException(
            "Select a running Visual Studio instance with a project file open.");

        ProjectDevCmdActiveProject? active = await _resolver.ResolveAsync(
            pid, context.Developer.RequireSolutionPath(), context.CancellationToken);
        if (active is null)
            return new(false, "Open a file in a loaded Visual Studio project, then press Dev Cmd again.");

        string devCmd = DeveloperCommandPromptLauncher.FindVsDevCmd(active.VisualStudioExecutablePath);
        ProcessStartInfo startInfo = DeveloperCommandPromptLauncher.CreateStartInfo(devCmd, active.ProjectDirectory);
        context.CancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
            "Could not launch the Developer Command Prompt.");
        return new(true, $"Opened Developer Command Prompt for {active.ProjectName} in {active.ProjectDirectory} " +
            $"(active file: {Path.GetFileName(active.FilePath)}).");
    }
}
