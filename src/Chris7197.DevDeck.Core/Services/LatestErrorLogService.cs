using System.Diagnostics;

namespace chris7197.DevDeck.Core;

/// <summary>Finds a log file and optionally opens it with the default Windows application.</summary>
public sealed class LatestErrorLogService(LogFileResolver resolver, DeckLog log)
{
    public string? FindLatest(string solutionPath) => resolver.FindLatest(solutionPath);

    public IReadOnlyList<string> FindRecent(string solutionPath, int count) => resolver.FindRecent(solutionPath, count);

    public string? OpenLatest(string solutionPath)
    {
        string? path = FindLatest(solutionPath);
        if (path is null)
        {
            log.Write("No error log found for solution: " + solutionPath);
            return null;
        }

        log.Write("Opening latest error log: " + path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return path;
    }
}
