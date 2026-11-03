using System.Text.Json;
using System.Text.RegularExpressions;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Investigation;

public sealed class InvestigationPlugin : IDevDeckPlugin
{
    public string Id => "com.chris7197.devdeck.investigation";
    public string Name => "Smart Investigation";

    public IReadOnlyList<IDevDeckOperation> CreateOperations(DevDeckPluginContext context)
    {
        var host = context.HostServices ?? throw new InvalidOperationException("Investigation requires Phase 5 host services.");
        var options = context.Configuration.Deserialize<InvestigationPluginSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        string pattern = options.AmbiguousCodePattern ?? host.Settings.SmartInvestigateAmbiguousCodePattern;
        if (string.IsNullOrWhiteSpace(pattern))
            throw new InvalidOperationException("AmbiguousCodePattern cannot be empty.");
        // Validate at discovery, not on the first button press.
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException error) { throw new InvalidOperationException("Invalid AmbiguousCodePattern regex.", error); }
        if (host.Investigations is null || host.Navigator is null || host.Prompts is null)
            throw new InvalidOperationException("Host investigation, navigation or prompt service is unavailable.");
        return [new SmartInvestigateOperation(host.Investigations, host.Navigator, host.Prompts, pattern)];
    }
}

public sealed class InvestigationPluginSettings
{
    public string? AmbiguousCodePattern { get; set; }
}
