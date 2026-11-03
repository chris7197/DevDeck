using System.Diagnostics;
using System.Runtime.InteropServices;

namespace chris7197.DevDeck.Core;

/// <summary>A foreground window snapshot. ProcessName is without the .exe suffix.</summary>
public sealed record ForegroundWindowInfo(string ProcessName, string Title);

public interface IForegroundWindowReader
{
    ForegroundWindowInfo? Read();
}

public sealed class WindowsForegroundWindowReader : IForegroundWindowReader
{
    public ForegroundWindowInfo? Read()
    {
        return ReadWindow(GetForegroundWindow());
    }

    /// <summary>Inspect the window from a foreground-change event, even after focus has moved.</summary>
    public ForegroundWindowInfo? ReadWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            return new ForegroundWindowInfo(process.ProcessName, GetWindowTitle(window));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new System.Text.StringBuilder(length + 1);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder title, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
}

public sealed class DeckProfileRule
{
    public string Profile { get; set; } = "Default";
    public string Process { get; set; } = "";
    public string? TitleContains { get; set; }

    public bool Matches(ForegroundWindowInfo info) =>
        info.ProcessName.Equals(Path.GetFileNameWithoutExtension(Process), StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(TitleContains) || info.Title.Contains(TitleContains, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Maps 15 fixed Stream Dock profile-slot actions to different operations based on the active app.
/// The hardware keys never move; their content/behaviour changes. No undocumented profile APIs.
/// </summary>
public sealed class DevDeckProfiles(DeckSettings settings, IForegroundWindowReader windowReader)
{
    public const string SlotPrefix = "com.chris7197.devdeck.slot";
    private string _currentProfile = "Default";
    private string? _manualProfile;
    private readonly object _sync = new();

    public string CurrentProfile { get { lock (_sync) return _manualProfile ?? _currentProfile; } }
    public string? ManualProfile { get { lock (_sync) return _manualProfile; } }
    public IReadOnlyList<string> AvailableProfiles => settings.Profiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string SlotId(int number) => number is >= 1 and <= 15
        ? $"{SlotPrefix}{number:00}" : throw new ArgumentOutOfRangeException(nameof(number));

    public static bool TrySlotNumber(string id, out int slot)
    {
        slot = 0;
        return id.StartsWith(SlotPrefix, StringComparison.Ordinal) &&
               id.Length == SlotPrefix.Length + 2 &&
               int.TryParse(id.AsSpan(SlotPrefix.Length), out slot) && slot is >= 1 and <= 15;
    }

    /// <summary>Force a profile for testing, or null to resume automatic detection.</summary>
    public void SetManualProfile(string? profile)
    {
        if (profile is not null && !settings.Profiles.ContainsKey(profile))
            throw new ArgumentException($"Unknown profile: {profile}", nameof(profile));
        lock (_sync) _manualProfile = profile;
    }

    public string DetectProfile(ForegroundWindowInfo? foreground)
    {
        if (foreground is null) return "Default";
        foreach (DeckProfileRule rule in settings.ProfileRules)
            if (rule.Matches(foreground)) return rule.Profile;
        return "Default";
    }

    public string Refresh()
    {
        ForegroundWindowInfo? foreground = windowReader.Read();
        lock (_sync)
        {
            if (_manualProfile is not null) return _manualProfile;
            string detected = DetectProfile(foreground);
            // A simulator, desktop shell or other unrelated window shouldn't flicker the
            // physical controller back to Default. Set false for strict auto-switching.
            if (detected != "Default" || !settings.RememberLastApplicationProfile)
                _currentProfile = detected;
            return _currentProfile;
        }
    }

    public string? Resolve(string actionOrSlotId)
    {
        if (!TrySlotNumber(actionOrSlotId, out int index)) return actionOrSlotId; // original actions remain usable
        lock (_sync)
        {
            string profile = _manualProfile ?? _currentProfile;
            if (!settings.Profiles.TryGetValue(profile, out List<string?>? slots)) return null;
            string? action = index <= slots.Count ? slots[index - 1] : null;
            return string.IsNullOrWhiteSpace(action) ? null : action;
        }
    }
}
