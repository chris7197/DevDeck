namespace chris7197.DevDeck.Core;

/// <summary>First matching solution-path rule wins. No search or fallback to another solution.</summary>
public static class VisualStudioTargetResolver
{
    public static T? Match<T>(IEnumerable<T> targets, string? solutionPath) where T : VisualStudioTargetSettings =>
        string.IsNullOrWhiteSpace(solutionPath) ? null : targets.FirstOrDefault(t => solutionPath.MatchesPattern(t.PathFilter));

    // Explicit per-key configuration is authoritative. A mismatching or incomplete
    // custom target must never launch a different target from the global rules.
    public static OpenProjectFileTarget? ResolveFileTarget(IEnumerable<OpenProjectFileTarget> targets,
        string? solutionPath, DeckActionSettings? options)
    {
        if (options?.ProjectFileUseCustom != true)
            return Match(targets, solutionPath);

        string filter = NormalizeFilter(options.ProjectFilePathFilter);
        if (string.IsNullOrWhiteSpace(solutionPath) || !solutionPath.MatchesPattern(filter)) return null;
        string? path = options.ProjectFilePath?.Trim();
        if (!IsValidRelativePath(path))
            throw new InvalidOperationException("Configure a valid solution-relative file path for this key.");
        string? editor = options.ProjectFileEditor?.Trim();
        if (editor is not null && editor.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Invalid Visual Studio editor name.");
        return new OpenProjectFileTarget
        {
            PathFilter = filter,
            Label = options.Label,
            FilePath = path!,
            Editor = string.IsNullOrWhiteSpace(editor) ? null : editor
        };
    }

    public static RunProjectTarget? ResolveRunTarget(IEnumerable<RunProjectTarget> targets,
        string? solutionPath, DeckActionSettings? options)
    {
        if (options?.RunProjectUseCustom != true)
            return Match(targets, solutionPath);

        string filter = NormalizeFilter(options.RunProjectPathFilter);
        if (string.IsNullOrWhiteSpace(solutionPath) || !solutionPath.MatchesPattern(filter)) return null;
        string? path = options.RunProjectPath?.Trim();
        if (!IsValidRelativePath(path) ||
            !path!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configure a valid solution-relative .csproj path for this key.");
        string buildMode = options.RunProjectBuildMode ?? "Rebuild";
        int timeout = options.RunProjectBuildTimeoutSeconds ?? 600;
        if (!IsValidBuildMode(buildMode) || timeout is < 10 or > 3600)
            throw new InvalidOperationException("Invalid build mode or timeout (10–3600 seconds).");
        return new RunProjectTarget
        {
            PathFilter = filter,
            Label = options.Label,
            ProjectPath = path!,
            BuildMode = buildMode,
            SaveAll = options.RunProjectSaveAll ?? true,
            Confirm = options.RunProjectConfirm ?? true,
            BuildTimeoutSeconds = timeout
        };
    }

    private static string NormalizeFilter(string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? "*" : filter.Trim();

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

    public static string Label(VisualStudioTargetSettings? target, string fallback, DeckActionSettings? options = null) =>
        !string.IsNullOrWhiteSpace(options?.Label) ? options!.Label! :
        string.IsNullOrWhiteSpace(target?.Label) ? fallback : target!.Label!;
}
