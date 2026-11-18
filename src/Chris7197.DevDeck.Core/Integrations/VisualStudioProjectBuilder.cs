using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>Build outside the COM STA; use the selected VS installation's MSBuild and require exit code zero.</summary>
public sealed class VisualStudioProjectBuilder(DeckLog log)
{
    public async Task<bool> BuildAsync(VisualStudioProjectBuildContext context, string mode, int timeoutSeconds, CancellationToken ct)
    {
        if (!VisualStudioTargetResolver.IsValidBuildMode(mode) || mode.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Build or Rebuild.", nameof(mode));
        string msbuild = FindMSBuild(context.ProcessId);
        var start = CreateStartInfo(context, msbuild, mode);
        ct.ThrowIfCancellationRequested();
        log.Write($"MSBuild {mode}: {context.ProjectPath} ({context.Configuration}|{context.Platform}); {msbuild}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) { log.WriteWarning("Could not terminate MSBuild: " + ex.Message); }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"MSBuild exceeded {timeoutSeconds} seconds. Debugging was not started.");
        }
        string output = (await stdout).Trim();
        string errors = (await stderr).Trim();
        if (output.Length > 0 || errors.Length > 0)
        {
            string message = "MSBuild output:" + Environment.NewLine + output + Environment.NewLine + errors;
            if (process.ExitCode == 0) log.Write(message); else log.WriteError(message);
        }
        log.Write($"MSBuild exited with code {process.ExitCode}.");
        return process.ExitCode == 0;
    }

    // Public to allow argument construction to be tested without running MSBuild or Visual Studio.
    public static ProcessStartInfo CreateStartInfo(VisualStudioProjectBuildContext context, string msbuild, string mode)
    {
        if (!VisualStudioTargetResolver.IsValidBuildMode(mode) || mode.Equals("None", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Build or Rebuild.", nameof(mode));
        string solution = context.SolutionPath;
        string directory = Path.GetDirectoryName(solution)!;
        var start = new ProcessStartInfo
        {
            FileName = msbuild,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Let MSBuild handle project references. Do not rebuild the entire solution.
        // ArgumentList preserves paths containing spaces without shell quoting/injection.
        foreach (string argument in new[] { context.ProjectPath, "/nologo", "/v:minimal", "/nr:false",
            mode.Equals("Rebuild", StringComparison.OrdinalIgnoreCase) ? "/t:Rebuild" : "/t:Build",
            "/p:Configuration=" + context.Configuration, "/p:Platform=" + context.Platform,
            "/p:SolutionDir=" + (Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar),
            "/p:SolutionPath=" + solution, "/p:SolutionName=" + Path.GetFileNameWithoutExtension(solution),
            "/p:SolutionFileName=" + Path.GetFileName(solution), "/p:SolutionExt=" + Path.GetExtension(solution) })
            start.ArgumentList.Add(argument);
        return start;
    }

    private static string FindMSBuild(uint pid)
    {
        using var process = Process.GetProcessById(checked((int)pid));
        if (!process.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected Visual Studio process has closed.");
        string exe = process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot locate the selected Visual Studio installation.");
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", ".."));
        string bin = Path.Combine(root, "MSBuild", "Current", "Bin");
        string[] candidates = [Path.Combine(bin, "amd64", "MSBuild.exe"), Path.Combine(bin, "MSBuild.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("MSBuild was not found in the selected Visual Studio installation.");
    }
}
