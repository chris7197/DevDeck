using System.Runtime.InteropServices;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Shortcuts;

/// <summary>
/// Sends a shortcut to the CURRENT foreground app only when it matches the expected profile.
/// Never tries to activate an arbitrary window or send keys to a remembered context.
/// </summary>
public sealed class ShortcutOperation(
    string id, string title, string shortcut, string iconName,
    string requiredProfile, DevDeckProfiles profiles, IForegroundWindowReader foreground) : IDevDeckOperation
{
    public string Name => title;
    public bool IsShortcut => true;
    public string PluginActionId => id;
    public string? IconName => iconName;
    public bool RequiresSolutionContext => false;

    public Task<DevDeckButtonState> GetConfiguredStateAsync(DeveloperContext? context,
        DeckActionSettings? actionSettings, CancellationToken cancellationToken = default)
    {
        string configured = ShortcutFor(actionSettings);
        return Task.FromResult(new DevDeckButtonState(Name, configured, DeckStatus.Neutral,
            $"{requiredProfile} keyboard shortcut: {configured}. Focus that application before pressing.", IconName: IconName));
    }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default) =>
        GetConfiguredStateAsync(context, null, cancellationToken);

    private string ShortcutFor(DeckActionSettings? actionSettings)
    {
        string configured = actionSettings?.GetPluginSettings<ShortcutButtonSettings>()?.Shortcut ?? shortcut;
        // Reject invalid shortcuts *before* generating Win32 keyboard events.
        ValidateShortcut(configured);
        return configured;
    }

    public sealed class ShortcutButtonSettings
    {
        public string? Shortcut { get; set; }
    }

    public static void ValidateShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) throw new ArgumentException("Shortcut cannot be blank.");
        string[] tokens = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 4) throw new ArgumentException("Invalid shortcut length.");
        foreach (string token in tokens) _ = KeyCode(token);
        if (tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count() != tokens.Length)
            throw new ArgumentException("Duplicate shortcut key.");
    }

    public Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        ForegroundWindowInfo? window = foreground.Read();
        if (window is null || !string.Equals(profiles.DetectProfile(window), requiredProfile, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new DevDeckOperationResult(false,
                $"Focus a {requiredProfile} window before using {Name}. No keys were sent."));
        try
        {
            string configured = ShortcutFor(context.ActionSettings);
            SendKeys(configured);
            return Task.FromResult(new DevDeckOperationResult(true, $"Sent {configured} to {window.ProcessName}: {window.Title}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DevDeckOperationResult(false, ex.Message));
        }
    }

    private static void SendKeys(string shortcut)
    {
        // Small, explicit vocabulary rather than executing arbitrary shell commands from settings.
        string[] tokens = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = tokens.Select(KeyCode).ToArray();
        if (keys.Length == 0) throw new ArgumentException("Empty shortcut.");
        foreach (byte key in keys) keybd_event(key, 0, 0, UIntPtr.Zero);
        for (int index = keys.Length - 1; index >= 0; index--)
            keybd_event(keys[index], 0, 2, UIntPtr.Zero);
    }

    private static byte KeyCode(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" => 0x11,
        "SHIFT" => 0x10,
        "ALT" => 0x12,
        "F5" => 0x74,
        "F12" => 0x7B,
        "N" => 0x4E,
        "M" => 0x4D,
        "R" => 0x52,
        _ => throw new ArgumentException($"Unsupported shortcut key '{token}'.")
    };

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
