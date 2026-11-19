using System.Text.Json.Serialization;

namespace chris7197.DevDeck.Core;

public enum EmbeddedStatementStyle
{
    Default,
    NewLine,
    SameLine
}

/// <summary>Optional house-style rules applied only after CSharpier succeeds.</summary>
public sealed class CSharpFormattingSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<EmbeddedStatementStyle>))]
    public EmbeddedStatementStyle EmbeddedStatementStyle { get; set; } = EmbeddedStatementStyle.Default;
    public bool KeepMethodSignaturesOnSingleLine { get; set; }
    public int SameLineMaxLength { get; set; } = 140;
    public int IndentSize { get; set; } = 4;
    public bool UseTabs { get; set; }

    [JsonIgnore]
    public bool IsEnabled => EmbeddedStatementStyle != EmbeddedStatementStyle.Default || KeepMethodSignaturesOnSingleLine;

    public void Validate()
    {
        if (!Enum.IsDefined(EmbeddedStatementStyle) || SameLineMaxLength is < 20 or > 10000 || IndentSize is < 1 or > 16)
            throw new InvalidOperationException("Invalid CSharpFormatting settings in settings.json.");
    }
}
