using System.Diagnostics;

namespace chris7197.DevDeck.Core;

public sealed class OpenInVsCodeOperation(DeckSettings settings, VisualStudioDocumentService documents) : IDevDeckOperation
{
    public string Name => "Open in VS Code";
    public string PluginActionId => "com.chris7197.devdeck.openvscode";
    public string? IconName => DevDeckIcons.OpenVsCode;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => true;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context, DeckActionSettings? options,
        CancellationToken cancellationToken = default) => Task.FromResult(new DevDeckButtonState(
            "VS Code", context?.SolutionName ?? "Focus VS", context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Open the solution in VS Code and, when available, the active Visual Studio file at the same cursor position",
            IconName: IconName));

    public Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context) => LaunchAsync(context);

    private async Task<DevDeckOperationResult> LaunchAsync(DevDeckOperationContext context)
    {
        string path = context.Developer.RequireSolutionPath();
        string dir = Path.GetDirectoryName(path)!;
        string? cli = VsCodeLauncher.FindCli();
        if (cli is null) throw new FileNotFoundException("VS Code CLI not found. Install the 'code' command in PATH, or install VS Code normally.");

        VisualStudioActiveDocument? active = null;
        if (context.Developer.VisualStudioProcessId is uint pid)
            active = await documents.GetActiveDocumentAsync(pid, path, context.CancellationToken);

        // One invocation opens the folder as the workspace AND jumps to the current source location.
        // For explicit CLI solution paths (no running VS PID), retain the old folder-only behaviour.
        var info = new ProcessStartInfo
        {
            FileName = cli,
            UseShellExecute = true,
            WorkingDirectory = dir
        };
        info.ArgumentList.Add("--reuse-window");
        if (active is not null)
        {
            info.ArgumentList.Add("--goto");
            info.ArgumentList.Add($"{active.FilePath}:{active.Line}:{active.Column}");
        }
        info.ArgumentList.Add(dir);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start VS Code.");

        bool wantsChat = context.ActionSettings?.FocusChat ?? settings.FocusVsCodeChat;
        string opened = active is null ? dir : $"{Path.GetFileName(active.FilePath)}:{active.Line}:{active.Column} in {dir}";
        string notice = active is { IsSaved: false } ? " Unsaved Visual Studio edits will not be present in VS Code." : "";
        if (wantsChat)
        {
            bool focused = await VsCodeLauncher.TryFocusChatAsync(dir, context.CancellationToken);
            return new(true, focused
                ? $"Opened {opened} in VS Code and focused Copilot Chat.{notice}"
                : $"Opened {opened} in VS Code. Couldn't safely focus Copilot Chat; press Ctrl+Alt+I in VS Code.{notice}");
        }
        return new(true, $"Opened {opened} in VS Code.{notice}");
    }
}
