using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

public sealed class OpenSolutionOperation(VisualStudioLocator visualStudio) : IDevDeckOperation
{
    public string Name => "Open Solution";
    public string PluginActionId => "com.chris7197.devdeck.opensolution";
    public string? IconName => DevDeckIcons.OpenSolution;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

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
