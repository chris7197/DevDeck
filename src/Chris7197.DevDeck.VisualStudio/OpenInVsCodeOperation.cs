using chris7197.DevDeck.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.VisualStudio;

public sealed class OpenInVsCodeOperation(DeckSettings settings, bool? pluginFocusChat) : IDevDeckOperation, IDevDeckCliCommandSource
{
    public IReadOnlyList<DevDeckCliCommand> GetCliCommands() =>
        [new("--open-vscode", "--open-vscode [solution.sln]", OpenCliAsync)];

    private async Task<int> OpenCliAsync(DevDeckCliContext cli)
    {
        if (cli.Arguments.Count is < 1 or > 2)
            return await cli.InvalidArgumentsAsync("--open-vscode [solution.sln]");
        DeveloperContext? context = await cli.ResolveContextAsync(
            cli.Arguments.Count == 2 ? cli.Arguments[1] : null, Name);
        return context is null ? 0 : await cli.RunOperationAsync(this, context);
    }

    public string Name => "Open in VS Code";
    public string PluginActionId => "com.chris7197.devdeck.openvscode";
    public string? IconName => DevDeckIcons.OpenVsCode;
    public bool RequiresSolutionContext => true;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? options,
        CancellationToken cancellationToken = default) => Task.FromResult(new DevDeckButtonState(
            "VS Code", context?.SolutionName ?? "Focus VS", context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Open the active Visual Studio solution directory in VS Code", IconName: IconName));

    public Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context) => LaunchAsync(context);

    private async Task<DevDeckOperationResult> LaunchAsync(DevDeckOperationContext context)
    {
        string path = context.Developer.RequireSolutionPath();
        string dir = Path.GetDirectoryName(path)!;
        string? cli = VsCodeLauncher.FindCli();
        if (cli is null) throw new FileNotFoundException("VS Code CLI not found. Install the 'code' command in PATH, or install VS Code normally.");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = true,
            WorkingDirectory = dir,
            ArgumentList = { "--reuse-window", dir }
        }) ?? throw new InvalidOperationException("Could not start VS Code.");

        bool wantsChat = context.ActionSettings?.FocusChat ?? pluginFocusChat ?? settings.FocusVsCodeChat;
        if (wantsChat)
        {
            bool focused = await VsCodeLauncher.TryFocusChatAsync(dir, context.CancellationToken);
            return new(true, focused
                ? $"Opened {dir} in VS Code and focused Copilot Chat."
                : $"Opened {dir} in VS Code. Couldn't safely focus Copilot Chat; press Ctrl+Alt+I in VS Code.");
        }
        return new(true, $"Opened {dir} in VS Code.");
    }
}
