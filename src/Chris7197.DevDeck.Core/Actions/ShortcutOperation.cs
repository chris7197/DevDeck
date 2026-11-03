using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Sends a shortcut to the CURRENT foreground app only when it matches the expected profile.
/// Never tries to activate an arbitrary window or send keys to a remembered context.
/// </summary>
public sealed class ShortcutOperation(
    string id, string title, string shortcut, string iconName,
    string requiredProfile, DevDeckProfiles profiles, IForegroundWindowReader foreground) : IDevDeckOperation
{
    public string Name => title;
    public string PluginActionId => id;
    public IReadOnlyList<string> CliCommands => [];
    public string? IconName => iconName;
    public string CliUsage => "";
    public bool RequiresSolutionContext => false;
    public bool TryParseCliArguments(IReadOnlyList<string> args, out string? target) { target = null; return false; }

    public Task<DevDeckButtonState> GetStateAsync(DeveloperContext? context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DevDeckButtonState(Name, shortcut, DeckStatus.Neutral,
            $"{requiredProfile} keyboard shortcut: {shortcut}. Focus that application before pressing.", IconName: IconName));

    public Task<DevDeckOperationResult> ExecuteAsync(DevDeckOperationContext context)
    {
        ForegroundWindowInfo? window = foreground.Read();
        if (window is null || !string.Equals(profiles.DetectProfile(window), requiredProfile, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new DevDeckOperationResult(false,
                $"Focus a {requiredProfile} window before using {Name}. No keys were sent."));
        try
        {
            SendKeys(shortcut);
            return Task.FromResult(new DevDeckOperationResult(true, $"Sent {shortcut} to {window.ProcessName}: {window.Title}"));
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
        "W" => 0x57,
        _ => throw new ArgumentException($"Unsupported shortcut key '{token}'.")
    };

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
