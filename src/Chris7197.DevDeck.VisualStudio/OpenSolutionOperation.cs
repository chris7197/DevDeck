using chris7197.DevDeck.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.VisualStudio;

public sealed class OpenSolutionOperation(VisualStudioLocator visualStudio) : IDevDeckOperation, IDevDeckCliCommandSource
{
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
        [new("--open-solution", "--open-solution <solution.sln>", OpenCliAsync)];

    private async Task<int> OpenCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count != 2)
            return await cli.InvalidArgumentsAsync("--open-solution <solution.sln>");
        return await cli.RunOperationAsync(this,
            new DeveloperContext(Environment.CurrentDirectory, null, null),
            new DeckActionSettings { SolutionPath = cli.Arguments[1] });
    }

    public string Name => "Open Solution";
    public string PluginActionId => "com.chris7197.devdeck.opensolution";
    public string? IconName => DevDeckIcons.OpenSolution;
    public bool RequiresSolutionContext => false;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? options,
        CancellationToken cancellationToken = default)
    {
        string? path = options?.SolutionPath;
        bool valid = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        return Task.FromResult(new DevDeckButtonState(options?.Label ?? (valid ? Path.GetFileNameWithoutExtension(path)! : "Open Solution"),
            valid ? "Open / Focus" : "Set path", valid ? DeckStatus.Neutral : DeckStatus.Unavailable,
            valid ? Path.GetFullPath(path!) : "Configure this button's full .sln/.slnx path.", IconName: IconName));
    }

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        string? path = context.ActionSettings?.SolutionPath;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Set this key's SolutionPath in its properties.");
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!File.Exists(path) || !(path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException("Configured solution must be an existing .sln or .slnx file.", path);
        IntPtr hwnd = await visualStudio.FindOpenSolutionWindowAsync(path);
        if (hwnd != IntPtr.Zero)
        {
            return WindowActivator.Activate(hwnd)
                ? new(true, "Focused the Visual Studio instance already showing " + path)
                : new(false, "Solution is already open, but Windows refused to focus its window. Use the taskbar: " + path);
        }
        // ShellExecute may return null when an existing application handles the association.
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new(true, "Started Visual Studio for " + path);
    }
}
