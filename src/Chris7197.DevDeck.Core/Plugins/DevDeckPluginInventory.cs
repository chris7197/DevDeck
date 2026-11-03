using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>Non-executing plugin inventory for the tray manager. It never loads plugin DLLs.</summary>
public static class DevDeckPluginInventory
{
    public sealed record Entry(string Id, string FolderName, string Folder, string AssemblyName,
        bool Enabled, string? Problem);

    public static IReadOnlyList<Entry> Scan(string hostDirectory)
    {
        var results = new List<Entry>();
        string root = Path.Combine(hostDirectory, "plugins");
        if (!Directory.Exists(root)) return results;
        foreach (string folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(folder, "plugin.json"))) continue;
            string name = Path.GetFileName(folder);
            string id = name;
            string assembly = "";
            bool enabled = true;
            string? problem = null;
            try
            {
                var manifest = JsonSerializer.Deserialize<DevDeckPluginLoader.PluginManifest>(
                    File.ReadAllText(Path.Combine(folder, "plugin.json")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true }) ?? throw new JsonException("Empty plugin manifest.");
                id = manifest.Id;
                assembly = manifest.Assembly;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(assembly) ||
                    Path.GetFileName(assembly) != assembly || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new JsonException("Invalid plugin ID or DLL filename.");
                var document = DevDeckPluginConfigurationStore.Open(folder);
                enabled = document.Values["Enabled"]?.GetValue<bool>() ?? true;
                var issues = DevDeckPluginConfigurationStore.Validate(folder, document.Values);
                if (issues.Count > 0) problem = string.Join("; ", issues);
                else if (enabled && !File.Exists(Path.Combine(folder, assembly))) problem = "Plugin DLL is missing.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            { problem = e.Message; }
            results.Add(new Entry(id, name, folder, assembly, enabled, problem));
        }
        return results;
    }
}

/// <summary>
/// Last observed host startup result, not a live COM probe. Kept outside the install folder
/// and keyed by full host path to prevent development/production status collisions.
/// </summary>
public static class DevDeckPluginHealthStore
{
    public sealed record Snapshot(DateTimeOffset CheckedAtUtc, List<DevDeckPluginInfo> Plugins);

    private static string PathFor(string hostDirectory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(hostDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "chris7197", "DevDeck", "PluginHealth", Convert.ToHexString(hash)[..20] + ".json");
    }

    public static void TryPublish(string hostDirectory, IReadOnlyList<DevDeckPluginInfo> plugins, DeckLog log)
    {
        string path = PathFor(hostDirectory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Snapshot(DateTimeOffset.UtcNow, plugins.ToList())));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { log.WriteWarning("Could not publish plugin health snapshot: " + e.Message); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    public static Snapshot? Read(string hostDirectory)
    {
        string path = PathFor(hostDirectory);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }
}
