namespace chris7197.DevDeck.Core;

public abstract class VisualStudioTargetSettings
{
    // Full solution path, case-insensitive; '*' works as in MigrationTargets.
    public string PathFilter { get; set; } = "";
    public string? Label { get; set; }
}

public sealed class OpenProjectFileTarget : VisualStudioTargetSettings
{
    public string FilePath { get; set; } = "";
    // Null/empty uses the default Visual Studio editor.
    public string? Editor { get; set; }
}

public sealed class RunProjectTarget : VisualStudioTargetSettings
{
    public string ProjectPath { get; set; } = "";
    public string BuildMode { get; set; } = "Rebuild";
    public bool SaveAll { get; set; } = true;
    public bool Confirm { get; set; } = true;
    public int BuildTimeoutSeconds { get; set; } = 600;
}
