using System.Diagnostics;
using System.Security.Cryptography;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Formats the saved active C# file with CSharpier. Verifies a real disk change instead of
/// equating process exit code zero with "file formatted". The VS editor may need to reload
/// external changes; never overwrite an unsaved editor buffer.
/// </summary>
public sealed class FormatCSharpOperation(VisualStudioDocumentService documents, DeckLog? diagnostics = null) : IDevDeckOperation
{
    private readonly DeckLog _log = diagnostics ?? new DeckLog();

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

        string filename = Path.GetFileName(active.FilePath);
        // A timestamp alone is not reliable: tools may rewrite a file with the same bytes,
        // and two writes can share a coarse filesystem timestamp.
        byte[] beforeHash = await HashAsync(active.FilePath, context.CancellationToken);
        var stopwatch = Stopwatch.StartNew();
        _log.WriteTrace($"C# format: starting pid={pid}, file={filename}, saved=true.");
        await context.ProgressAsync($"Formatting {filename}...");

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
            _log.WriteTrace($"C# format: aborted after {stopwatch.ElapsedMilliseconds}ms.");
            throw;
        }

        string output = await stdout;
        string error = await stderr;
        _log.WriteTrace($"C# format: formatter exit={process.ExitCode}, stdoutChars={output.Length}, " +
            $"stderrChars={error.Length}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        // Log only our own wrapper's provenance lines, not arbitrary formatter output
        // (which could include source code or private paths in an error message).
        foreach (string line in output.Split('\n'))
        {
            string detail = line.Trim();
            if (detail.StartsWith("Configuration: ", StringComparison.Ordinal) ||
                detail.StartsWith("Invocation: ", StringComparison.Ordinal))
                _log.WriteTrace("C# format: " + detail);
        }
        if (process.ExitCode != 0)
        {
            string details = string.Join(" ", new[] { output, error }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            if (details.Length > 1500) details = details[..1500] + "...";
            return new(false, $"CSharpier failed (exit {process.ExitCode}): {details}");
        }

        if (!File.Exists(active.FilePath))
            return new(false, $"CSharpier exited successfully but {filename} no longer exists.");
        byte[] afterHash = await HashAsync(active.FilePath, context.CancellationToken);
        if (CryptographicOperations.FixedTimeEquals(beforeHash, afterHash))
        {
            _log.WriteTrace($"C# format: NO disk change for {filename}; already formatted, ignored, " +
                "or CSharpier did not write the file.");
            return new(true,
                $"No changes to {filename}. It may already match CSharpier's style, or be excluded by an ignore rule. " +
                "Check the DevDeck tray log for details.", NoChanges: true);
        }

        _log.WriteTrace($"C# format: verified disk content changed for {filename} " +
            $"in {stopwatch.ElapsedMilliseconds}ms; Visual Studio reload is outside this operation.");
        return new(true, $"CSharpier changed {filename} on disk. If the editor still looks unchanged, " +
            "enable Visual Studio > Tools > Options > Environment > Documents > " +
            "Reload modified files unless there are unsaved changes.");
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await SHA256.HashDataAsync(stream, cancellationToken);
    }
}
