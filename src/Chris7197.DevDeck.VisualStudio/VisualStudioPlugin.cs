using System.Text.Json;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.VisualStudio;

/// <summary>
/// Visual Studio and VS Code action DLL. Uses the host's existing COM-safe services;
/// never creates an independent COM dispatcher, DTE object or STA worker.
/// </summary>
public sealed class VisualStudioPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.visualstudio";
    public string Name => "Visual Studio and VS Code";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException(
            "This Visual Studio plugin requires DevDeck host services (Phase 4 or newer).");
        var config = context.Configuration.Deserialize<VisualStudioPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return
        [
            new OpenSolutionOperation(host.VisualStudio),
            new NewMigrationOperation(host.Migrations, host.Statuses),
            new OpenInVsCodeOperation(host.Settings, config.FocusVsCodeChat)
        ];
    }
}

public sealed class VisualStudioPluginSettings
{
    // Null retains the legacy main settings.json behaviour; per-key FocusChat takes precedence.
    public bool? FocusVsCodeChat { get; set; }
}
