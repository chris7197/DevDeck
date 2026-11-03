using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>Format one saved, active C# source file using the bundled standalone PowerShell wrapper.</summary>
public sealed class FormatCSharpOperation(VisualStudioDocumentService documents) : IDevDeckOperation
{
    public string Name => "Format C# File";
    public string PluginActionId => "com.chris7197.devdeck.csharpier.format";
    public string? IconName => DevDeckIcons.FormatCSharp;
    public IReadOnlyList<string> CliCommands => [];
    public string CliUsage => "";
    public bool RequiresSolutionContext => true;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DevDeckButtonState("CSharpier", context?.SolutionName ?? "Focus VS",
            context is null ? DeckStatus.Unavailable : DeckStatus.Neutral,
            "Format only the saved active C# file. Requires a local or global CSharpier dotnet tool.", IconName: IconName));

    public async Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        uint pid = context.Developer.VisualStudioProcessId ?? throw new InvalidOperationException(
            "Select a running Visual Studio instance containing the C# file to format.");
        VisualStudioActiveDocument? active = await documents.GetActiveDocumentAsync(pid,
            context.Developer.RequireSolutionPath(), context.CancellationToken);
        if (active is null || !active.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return new(false, "Open a saved .cs document in the selected Visual Studio instance first.");
        if (!active.IsSaved)
            return new(false, "This C# document has unsaved changes. Save it in Visual Studio before running CSharpier, so external formatting cannot overwrite your work.");

        string script = Path.Combine(AppContext.BaseDirectory, "scripts", "Format-CSharp.ps1");
        if (!File.Exists(script))
            return new(false, $"Bundled CSharpier script is missing: {script}. Publish DevDeck again.");

        await context.ProgressAsync($"Formatting {Path.GetFileName(active.FilePath)}...");
        var info = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = Path.GetDirectoryName(active.FilePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", script, "-Path", active.FilePath })
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(context.CancellationToken);
        try
        {
            await process.WaitForExitAsync(context.CancellationToken).WaitAsync(TimeSpan.FromMinutes(3), context.CancellationToken);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        string message = string.Join(" ", new[] { await stdout, await stderr }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        if (process.ExitCode != 0)
            return new(false, $"CSharpier failed (exit {process.ExitCode}): {message}");
        return new(true, $"Formatted {Path.GetFileName(active.FilePath)} with CSharpier. " +
            "If Visual Studio prompts about an external file change, reload the file to see it.");
    }
}
