using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>Interface for independently built DevDeck action assemblies.</summary>
public interface IDevDeckPlugin
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context);
}

/// <summary>Plugin-wide settings plus safely shared host services (not independently created COM objects).</summary>
public sealed record DevDeckPluginContext(
    JsonElement Configuration,
    DevDeckProfiles Profiles,
    IForegroundWindowReader ForegroundWindow,
    DeckLog Log,
    DevDeckPluginHostServices? HostServices = null);

/// <summary>
/// References to host-owned services. DLLs do not create extra COM dispatchers or STA workers.
/// Optional in the plugin-loader API so older external plugins and loader tests keep working.
/// </summary>
public sealed record DevDeckPluginHostServices(
    DeckSettings Settings,
    VisualStudioLocator VisualStudio,
    NewMigrationService Migrations,
    DevDeckStatusProviders Statuses,
    SmartInvestigationService? Investigations = null,
    VisualStudioSourceNavigator? Navigator = null,
    IDevDeckPromptService? Prompts = null);

public sealed record DevDeckPluginInfo(string Id, string Name, string Folder, int ActionCount,
    bool Loaded, string? Error = null);
