using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace chris7197.DevDeck.Core;

public sealed class MigrationTargetSettings
{
    /// <summary>
    /// Solutions files whose path contains this string will be considered as migration projects.
    /// </summary>
    public string PathFilter { get; set; } = "";
    /// <summary>Path to the .csproj, RELATIVE to the folder containing the .sln.</summary>
    public string ProjectPath { get; set; } = "";
    /// <summary>Optional. Defaults to the project RootNamespace / project file name.</summary>
    public string? RootNamespace { get; set; }
    /// <summary>
    /// Optional. Defaults to the project folder. If you want to put migrations in a subfolder, specify it here, RELATIVE to the project folder.
    /// </summary>
    public string RelativeMigratorFolder { get; set; } = "";
    /// <summary>
    /// Optional. Defaults to 1. The line number to open the new migration file at in Visual Studio.
    /// </summary>
    public int OpenAtLine { get; set; } = Consts.OpenAtLineDefault;
    /// <summary>
    /// Optional. Defaults to 1. The offset to open the new migration file at in Visual Studio.
    /// </summary>
    public int OpenAtOffset { get; set; } = Consts.OpenAtOffsetDefault;
}

public sealed record MigrationProject(string ProjectFile, string RootNamespace, string RelativeMigrationFolder = "", int OpenAtLine = Consts.OpenAtLineDefault, int OpenAtOffset = Consts.OpenAtOffsetDefault)
{
    public string ProjectDirectory => Path.GetDirectoryName(ProjectFile)!;
    public string MigratorDirectory => Path.Combine(ProjectDirectory, RelativeMigrationFolder);
}

/// <summary>Choose a migrator project in the current solution; never silently choose among multiple candidates.</summary>
public sealed class MigrationProjectResolver(DeckSettings settings)
{
    public MigrationProject Resolve(string solutionPath)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var configured = settings.MigrationTargets.FirstOrDefault(
            entry => solutionPath.MatchesPattern(entry.PathFilter));

        // Use the explicitly configured project path, if provided.
        if (!string.IsNullOrWhiteSpace(configured?.ProjectPath))
        {
            string project = Path.GetFullPath(Path.Combine(baseDir, configured.ProjectPath));

            if (!File.Exists(project))
                throw new FileNotFoundException(
                    $"MigrationTargets['{configured.PathFilter}'].ProjectPath does not point to a project. Edit plugin/settings.json.", project);

            return new MigrationProject(
                project,
                GetNamespace(project, configured.RootNamespace),
                configured.RelativeMigratorFolder,
                configured.OpenAtLine,
                configured.OpenAtOffset);
        }

        // Otherwise, automatically discover the migration project.
        IEnumerable<string> projects = ReadSolutionProjects(solutionPath, baseDir);

        string[] candidates = projects.Where(p =>
            (Path.GetFileNameWithoutExtension(p).Contains("Migrat", StringComparison.OrdinalIgnoreCase) ||
             Path.GetDirectoryName(p)!.Split(Path.DirectorySeparatorChar).Any(s => s.Contains("DBMigrator", StringComparison.OrdinalIgnoreCase))) &&
            !Path.GetFileNameWithoutExtension(p).Contains("Test", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException(
                $"No migration project identified in '{solutionPath}'. Add a MigrationTargets entry in plugin/settings.json.");

        if (candidates.Length > 1)
            throw new InvalidOperationException(
                "Multiple possible migration projects. Set MigrationTargets.ProjectPath for this solution: " +
                string.Join(", ", candidates.Select(Path.GetFileName)));

        string discoveredProject = candidates[0];

        return new MigrationProject(
            discoveredProject,
            GetNamespace(discoveredProject, configured?.RootNamespace),
            configured?.RelativeMigratorFolder ?? string.Empty,
            configured?.OpenAtLine ?? Consts.OpenAtLineDefault,
            configured?.OpenAtOffset ?? Consts.OpenAtOffsetDefault);
    }

    private static IEnumerable<string> ReadSolutionProjects(string solutionPath, string baseDir)
    {
        string contents = File.ReadAllText(solutionPath);
        IEnumerable<string> paths;
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            paths = XDocument.Parse(contents).Descendants()
                .Where(x => x.Name.LocalName == "Project")
                .Select(x => (string?)x.Attribute("Path") ?? "")
                .Where(x => x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        else
            paths = Regex.Matches(contents, "\"(?<path>[^\"]+\\.csproj)\"", RegexOptions.IgnoreCase)
                .Cast<Match>().Select(m => m.Groups["path"].Value);

        foreach (string relativePath in paths)
        {
            string path = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string fullPath;
            try { fullPath = Path.GetFullPath(Path.Combine(baseDir, path)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            yield return fullPath;
        }
    }

    private static string GetNamespace(string projectFile, string? configuredNamespace)
    {
        if (!string.IsNullOrWhiteSpace(configuredNamespace)) return configuredNamespace.Trim();
        var project = XDocument.Load(projectFile);
        string? name = project.Descendants().FirstOrDefault(x => x.Name.LocalName == "RootNamespace")?.Value;
        if (string.IsNullOrWhiteSpace(name) || name.Contains("$(", StringComparison.Ordinal))
            name = Path.GetFileNameWithoutExtension(projectFile);
        return name;
    }
}
