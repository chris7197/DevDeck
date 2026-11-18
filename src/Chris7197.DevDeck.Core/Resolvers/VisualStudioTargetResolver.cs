namespace chris7197.DevDeck.Core;

/// <summary>First matching solution-path rule wins. No search or fallback to another solution.</summary>
public static class VisualStudioTargetResolver
{
    public static T? Match<T>(IEnumerable<T> targets, string? solutionPath) where T : VisualStudioTargetSettings =>
        string.IsNullOrWhiteSpace(solutionPath) ? null : targets.FirstOrDefault(t => solutionPath.MatchesPattern(t.PathFilter));

    public static string ResolvePath(string solutionPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Target path must be relative to the solution folder.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        string path = Path.GetFullPath(relativePath, directory);
        if (!File.Exists(path)) throw new FileNotFoundException("Configured target does not exist.", path);
        return path;
    }

    public static bool IsValidBuildMode(string? value) =>
        value is not null && (value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Rebuild", StringComparison.OrdinalIgnoreCase));

    public static bool IsValidRelativePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) &&
        value.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    public static string Label(VisualStudioTargetSettings target, string fallback) =>
        string.IsNullOrWhiteSpace(target.Label) ? fallback : target.Label;
}
