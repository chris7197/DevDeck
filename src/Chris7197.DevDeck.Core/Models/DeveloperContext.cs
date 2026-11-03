namespace chris7197.DevDeck.Core;

/// <summary>
/// Information about the development target for a single button press / CLI invocation.
/// Additional optional values (clipboard, branch, database, etc.) can be added here
/// without changing how the controller and CLI execute actions.
/// </summary>
public sealed record DeveloperContext(
    string WorkingDirectory,
    string? SolutionPath,
    string? SolutionName)
{
    public uint? VisualStudioProcessId { get; init; }

    public string? SolutionDirectory => SolutionPath is null ? null : Path.GetDirectoryName(SolutionPath);

    public string RequireSolutionPath() => SolutionPath ?? throw new InvalidOperationException(
        "This action requires a saved Visual Studio solution. Focus Visual Studio or pass the .sln/.slnx path.");
}

/// <summary>Resolves the active Visual Studio solution once for each invocation, or an explicit CLI target.</summary>
public sealed class DeveloperContextProvider(VisualStudioLocator visualStudio)
{
    public async Task<DeveloperContext> ResolveAsync(string? target, bool saveAll = false)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            string solution = await visualStudio.GetActiveSolutionPathAsync(saveAll);
            return FromSolution(solution);
        }

        string path = Path.GetFullPath(target);
        if (Directory.Exists(path)) return new DeveloperContext(path, null, null);

        if (File.Exists(path) &&
            (Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
            return FromSolution(path);

        throw new FileNotFoundException("Expected an existing repository directory or saved .sln/.slnx file.", path);
    }

    /// <summary>Read solution context for the VS process captured by a foreground-change event.</summary>
    public async Task<DeveloperContext> ResolveVisualStudioProcessAsync(uint processId)
    {
        string solution = await visualStudio.GetSolutionPathForProcessIdAsync(processId);
        return FromSolution(solution);
    }

    private static DeveloperContext FromSolution(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Solution does not exist.", fullPath);
        return new DeveloperContext(Path.GetDirectoryName(fullPath)!, fullPath, Path.GetFileNameWithoutExtension(fullPath));
    }
}
