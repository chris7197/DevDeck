using System.Diagnostics;

namespace chris7197.DevDeck.Core;

public sealed record GitOutput(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string Error => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public sealed class GitProcess(DeckLog log, int timeoutSeconds, bool logCommands = true)
{
    public async Task<GitOutput> RunAsync(string directory, CancellationToken cancellationToken, params string[] args)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);

        // Never open interactive credential or merge editor prompts.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        start.Environment["GIT_PAGER"] = "cat";

        if (logCommands) log.Write("git " + string.Join(" ", args));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start git.exe.");
            // Read both pipes concurrently. Don't cancel the readers before the child is killed;
            // that risks leaving unread output or unobserved tasks behind.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Preserve the distinction between an explicit shutdown and a command timeout.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { /* Already exited. */ }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    log.Write("Git process tree termination failed: " + ex.Message);
                }
                try
                {
                    using var reapTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(reapTimeout.Token);
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(reapTimeout.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                    log.Write("Git child process did not cleanly exit/drain after interruption: " + ex.Message);
                }

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Git command cancelled. Check the repository before retrying: " +
                        string.Join(" ", args), cancellationToken);
                throw new TimeoutException($"Git command exceeded {timeoutSeconds} seconds; process terminated. " +
                    "Check the repository before retrying: " + string.Join(" ", args));
            }

            // A hook/child can inherit redirected pipe handles after git itself exits.
            // Bound the final drain too, rather than hanging the controller indefinitely.
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var result = new GitOutput(process.ExitCode, await stdoutTask, await stderrTask);
            if (!result.Ok && logCommands) log.Write($"git exit {result.ExitCode}: {result.Error}");
            return result;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("git.exe was not found or couldn't be launched. " +
                "Install Git for Windows and add it to PATH.", e);
        }
    }
}
