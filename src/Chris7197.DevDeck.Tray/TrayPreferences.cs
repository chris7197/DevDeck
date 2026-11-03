using Microsoft.Win32;
using System.Text.Json;

namespace chris7197.DevDeck.Tray;

public sealed class TrayPreferences
{
    public bool NotificationsEnabled { get; set; } = true;
    public bool ShowDetailsInNotifications { get; set; } = true;
    public bool NotifyWarnings { get; set; }
    public string? PluginHostDirectory { get; set; }
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "chris7197", "DevDeck", "tray-settings.json");

    public static TrayPreferences Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<TrayPreferences>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save tray preferences: {ex.Message}", "DevDeck Tray", MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "chris7197.DevDeck.Tray";

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        string? value = key?.GetValue(ValueName) as string;
        return string.Equals(value, $"\"{Application.ExecutablePath}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Couldn't open Windows startup registry key.");
        if (enabled) key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
