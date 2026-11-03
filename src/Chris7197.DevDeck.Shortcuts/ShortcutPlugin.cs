using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Shortcuts;

/// <summary>Example external action provider. All six legacy action IDs are retained.</summary>
public sealed class ShortcutPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.shortcuts";
    public string Name => "SQL and browser shortcuts";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var settings = context.Configuration.Deserialize<ShortcutPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Shortcuts config cannot be empty.");
        if (settings.Actions is null || settings.Actions.Count == 0)
            throw new InvalidOperationException("Configure at least one shortcut action.");
        var result = new List<IDevDeckOperation>();
        foreach (var entry in settings.Actions)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) ||
                !entry.Id.StartsWith("com.chris7197.devdeck.", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                string.IsNullOrWhiteSpace(entry.RequiredProfile) ||
                string.IsNullOrWhiteSpace(entry.IconName))
                throw new InvalidOperationException("Invalid shortcut action entry in config.json.");
            ShortcutOperation.ValidateShortcut(entry.Shortcut);
            if (!context.Profiles.AvailableProfiles.Contains(entry.RequiredProfile, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Shortcut {entry.Id} references unknown profile {entry.RequiredProfile}.");
            result.Add(new ShortcutOperation(entry.Id, entry.Name, entry.Shortcut,
                entry.IconName, entry.RequiredProfile, context.Profiles, context.ForegroundWindow));
        }
        return result;
    }
}

public sealed class ShortcutPluginSettings
{
    public List<ShortcutActionDefinition> Actions { get; set; } = [];
}

public sealed class ShortcutActionDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string IconName { get; set; } = "";
    public string RequiredProfile { get; set; } = "";
}
