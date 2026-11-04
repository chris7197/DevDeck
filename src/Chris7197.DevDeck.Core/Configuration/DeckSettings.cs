using System.Text.Json;
using System.Text.Json.Nodes;

namespace chris7197.DevDeck.Core;

public sealed class DeckSettings
{
    public TeamsAuthSettings Teams { get; set; } = new();
    public SqlFormattingSettings SqlFormatting { get; set; } = new();
    // Optional manual SQL lookups for product/accessory identifiers. Keep project schemas private.
    // Keys: ProductCode, AccessoryCode. Use {code} as the quoted value placeholder.
    public Dictionary<string, string> InvestigationSqlLookups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<DbFlightRecorderTarget> DbFlightRecorderTargets { get; set; } = [];
    // First matching solution-path pattern wins. Paths may be files or directories.
    public List<LogMappingSettings> LogMappings { get; set; } = [];
    public List<DbFlightRecorderConfigMapping> DbFlightRecorderConfigMappings { get; set; } = [];
    // Optional per-slot defaults for the simulator and dynamic profile slots.
    // Key format: "VisualStudio/5" or "Default/2" (1-based slot number).
    public Dictionary<string, DeckActionSettings> ButtonPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FocusVsCodeChat { get; set; } = true;
    // For alphanumeric identifiers use, for example, "^[A-Z0-9]{6}$".
    public string SmartInvestigateAmbiguousCodePattern { get; set; } = @"^\d{6}$";
    public string Remote { get; set; } = "origin";
    public string MainBranch { get; set; } = "main";
    public string CommitMessage { get; set; } = "Pre merge";
    public bool SaveAllBeforeCommit { get; set; } = true;
    public int GitCommandTimeoutSeconds { get; set; } = 180;
    public int LiveRefreshSeconds { get; set; } = 10;
    /// <summary>Age limit for locally generated SQL and clipboard reports.</summary>
    public int ReportRetentionDays { get; set; } = 30;
    /// <summary>Maximum completed captures retained in each report category.</summary>
    public int MaxReportsPerKind { get; set; } = 200;
    /// <summary>Maximum concurrent controller operations. Project-changing operations remain exclusive.</summary>
    public int MaxConcurrentActions { get; set; } = 3;

    /// <summary>Automatically keep the last recognised app profile while Desktop/DevDeck is foreground.</summary>
    public bool RememberLastApplicationProfile { get; set; } = true;
    // First matching rule wins. Override these generic browser rules in settings.local.json.
    public List<DeckProfileRule> ProfileRules { get; set; } =
    [
        new() { Profile = "VisualStudio", Process = "devenv" },
        new() { Profile = "SqlServer", Process = "ssms" },
        new() { Profile = "BrowserBi", Process = "msedge", TitleContains = "Dashboard" },
        new() { Profile = "BrowserBi", Process = "chrome", TitleContains = "Dashboard" }
    ];
    // Each array index maps to a physical profile-slot button (1 to 15).
    // Empty/null means unassigned. Values must be registered action IDs.
    public Dictionary<string, List<string?>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Default"] = [],
        ["VisualStudio"] = ["com.chris7197.devdeck.syncmain", "com.chris7197.devdeck.latesterrorlog",
            "com.chris7197.devdeck.lasterror", "com.chris7197.devdeck.newmigration",
            "com.chris7197.devdeck.openvscode", null, null, null, null, null, null,
            "com.chris7197.devdeck.closeothers", "com.chris7197.devdeck.csharpier.format",
            "com.chris7197.devdeck.sql.format", "com.chris7197.devdeck.projectdevcmd"],
        ["SqlServer"] = ["com.chris7197.devdeck.ssms.newquery", "com.chris7197.devdeck.ssms.execute",
            "com.chris7197.devdeck.ssms.plan", null, null, null, null, null, null, null, null,
            "com.chris7197.devdeck.closeothers", "com.chris7197.devdeck.sql.format"],
        ["BrowserBi"] = ["com.chris7197.devdeck.browser.refresh", "com.chris7197.devdeck.browser.devtools",
            "com.chris7197.devdeck.browser.hardrefresh"]
    };
    // "*" selects the most recently modified FILE of any type in the mapped folder.
    // Use "*.log" if the folder also contains unrelated files.
    public string LogFilePattern { get; set; } = "*";

    // Bound tail reading for log files that may be actively written / very large.
    public int LastErrorMaxReadBytes { get; set; } = 4 * 1024 * 1024;
    public int LastErrorSearchFileCount { get; set; } = 10;
    // Solution path patterns support * wildcards (case-insensitive).
    // ProjectPath is RELATIVE to the solution folder. Omit mapping if the solution
    // contains exactly one project with "Migrat" in its name.
    public List<MigrationTargetSettings> MigrationTargets { get; set; } = new();
    public static DeckSettings Load(string baseDirectory)
    {
        // Keep shareable defaults in settings.json. Machine-, customer- and project-specific
        // overrides belong in settings.local.json, which is ignored by Git. Nested objects
        // are merged recursively; arrays are replaced so rule order remains predictable.
        JsonObject merged = ReadObject(Path.Combine(baseDirectory, "settings.json")) ?? new JsonObject();
        JsonObject? local = ReadObject(Path.Combine(baseDirectory, "settings.local.json"));
        if (local is not null) Merge(merged, local);
        DeckSettings settings = merged.Deserialize<DeckSettings>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new DeckSettings();
        settings.InvestigationSqlLookups = new Dictionary<string, string>(
            settings.InvestigationSqlLookups ?? new(), StringComparer.OrdinalIgnoreCase);
        if (settings.InvestigationSqlLookups.Any(x => string.IsNullOrWhiteSpace(x.Key) ||
            string.IsNullOrWhiteSpace(x.Value)))
            throw new InvalidOperationException("Invalid InvestigationSqlLookups in settings.json/settings.local.json.");
        settings.ButtonPresets = new Dictionary<string, DeckActionSettings>(
            settings.ButtonPresets ?? new(), StringComparer.OrdinalIgnoreCase);
        if (settings.SqlFormatting is null)
            throw new InvalidOperationException("Invalid SqlFormatting settings.");
        if (settings.Teams is null || settings.Teams.PollSeconds is < 15 or > 3600 ||
            settings.ButtonPresets.Any(p => p.Value is null))
            throw new InvalidOperationException("Invalid Teams/ButtonPresets settings.");
        if (settings.DbFlightRecorderTargets is null || settings.DbFlightRecorderTargets.Any(t =>
            t is null || string.IsNullOrWhiteSpace(t.Name) || string.IsNullOrWhiteSpace(t.Server) ||
            string.IsNullOrWhiteSpace(t.Database)) ||
            settings.DbFlightRecorderTargets.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != settings.DbFlightRecorderTargets.Count)
            throw new InvalidOperationException("Invalid or duplicate DbFlightRecorderTargets in settings.json.");
        if (settings.LogMappings is null || settings.LogMappings.Any(m => m is null ||
            string.IsNullOrWhiteSpace(m.PathFilter) || string.IsNullOrWhiteSpace(m.RelativePath)))
            throw new InvalidOperationException("Invalid LogMappings in settings.json/settings.local.json.");
        if (settings.DbFlightRecorderConfigMappings is null ||
            settings.DbFlightRecorderConfigMappings.Any(m => m is null ||
                string.IsNullOrWhiteSpace(m.PathFilter) ||
                string.IsNullOrWhiteSpace(m.ConfigPath) ||
                string.IsNullOrWhiteSpace(m.ConnectionStringName)))
            throw new InvalidOperationException("Invalid DbFlightRecorderConfigMappings entries in settings.json.");
        // JsonSerializer normally creates dictionaries with the default case-sensitive comparer.
        settings.Profiles = new Dictionary<string, List<string?>>(settings.Profiles ?? new(), StringComparer.OrdinalIgnoreCase);
        if (settings.ProfileRules is null || settings.Profiles.Count == 0 ||
            !settings.Profiles.ContainsKey("Default") ||
            settings.Profiles.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null || p.Value.Count > 15) ||
            settings.ProfileRules.Any(rule => rule is null || string.IsNullOrWhiteSpace(rule.Profile) ||
                !settings.Profiles.ContainsKey(rule.Profile) || string.IsNullOrWhiteSpace(rule.Process)))
            throw new InvalidOperationException("Invalid profile configuration in settings.json.");
        if (string.IsNullOrWhiteSpace(settings.Remote) ||
            string.IsNullOrWhiteSpace(settings.MainBranch) ||
            string.IsNullOrWhiteSpace(settings.CommitMessage) ||
            settings.GitCommandTimeoutSeconds is < 10 or > 3600 ||
            settings.LiveRefreshSeconds is < 3 or > 300 ||
            settings.ReportRetentionDays is < 1 or > 3650 ||
            settings.MaxReportsPerKind is < 5 or > 10000 ||
            settings.MaxConcurrentActions is < 1 or > 10 ||
            settings.LastErrorMaxReadBytes is < 65536 or > 33554432 ||
            settings.LastErrorSearchFileCount is < 1 or > 100 ||
            settings.MigrationTargets is null ||
            settings.MigrationTargets.Any(x => string.IsNullOrWhiteSpace(x.PathFilter) ||
                (!string.IsNullOrWhiteSpace(x.ProjectPath) &&
                !x.ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) ||
            string.IsNullOrWhiteSpace(settings.LogFilePattern) ||
            settings.LogFilePattern.IndexOfAny(new[] { '/', '\\' }) >= 0)
            throw new InvalidOperationException("Invalid settings.json values.");
        return settings;
    }

    private static JsonObject? ReadObject(string path)
    {
        if (!File.Exists(path)) return null;
        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return parsed as JsonObject ?? throw new InvalidOperationException($"Expected a JSON object in '{path}'.");
    }
    private static void Merge(JsonObject target, JsonObject overrides)
    {
        foreach (KeyValuePair<string, JsonNode?> entry in overrides)
        {
            if (entry.Value is JsonObject nested && target[entry.Key] is JsonObject existing)
                Merge(existing, nested);
            else
                target[entry.Key] = entry.Value?.DeepClone();
        }
    }
}
/// <summary>Shared SQL formatting settings; dbo removal is opt-in/out per key.</summary>
public sealed class SqlFormattingSettings
{
    public bool RemoveDboPrefixes { get; set; } = true;
}
