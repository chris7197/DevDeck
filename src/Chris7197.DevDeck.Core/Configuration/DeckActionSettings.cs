using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>Configuration belonging to ONE key instance, not to the action type.</summary>
public sealed record DeckActionSettings
{
    public string? Label { get; init; }
    public string? DbTarget { get; init; }
    public string? TeamId { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelUrl { get; init; }
    public string? SolutionPath { get; init; }
    public bool? FocusChat { get; init; }
    public bool? SqlRemoveDboPrefixes { get; init; }

    public static DeckActionSettings? Parse(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind != JsonValueKind.Object) return null;
        return JsonSerializer.Deserialize<DeckActionSettings>(value.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}

public sealed class TeamsAuthSettings
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public int PollSeconds { get; set; } = 60;
}
