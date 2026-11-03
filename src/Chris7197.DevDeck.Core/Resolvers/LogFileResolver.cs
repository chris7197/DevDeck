namespace chris7197.DevDeck.Core;

/// <summary>A configurable FILE or DIRECTORY mapping relative to the solution folder.</summary>
public sealed class LogMappingSettings
{
    public string PathFilter { get; set; } = "";
    public string RelativePath { get; set; } = "";
}

/// <summary>Resolves application logs without embedding project names or log paths in source.</summary>
public sealed class LogFileResolver(DeckSettings settings)
{
    public string? FindLatest(string solutionPath) => FindRecent(solutionPath, 1).FirstOrDefault();

    public IReadOnlyList<string> FindRecent(string solutionPath, int count)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || !File.Exists(solutionPath))
            throw new FileNotFoundException("The Visual Studio solution file could not be found.", solutionPath);
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));

        string fullSolutionPath = Path.GetFullPath(solutionPath);
        LogMappingSettings? mapping = settings.LogMappings.FirstOrDefault(x =>
            fullSolutionPath.MatchesPattern(x.PathFilter));
        if (mapping is null)
            throw new InvalidOperationException(
                $"No log mapping configured for '{Path.GetFileName(fullSolutionPath)}'. Add a LogMappings entry to settings.local.json.");

        string absolutePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(fullSolutionPath)!, mapping.RelativePath));
        if (File.Exists(absolutePath)) return [absolutePath];
        if (!Directory.Exists(absolutePath)) return [];

        return new DirectoryInfo(absolutePath)
            .EnumerateFiles(settings.LogFilePattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(file => file.FullName)
            .ToArray();
    }
}
