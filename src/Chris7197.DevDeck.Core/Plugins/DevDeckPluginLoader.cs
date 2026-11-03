using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Discovers trusted, explicitly declared action DLLs in plugins/{name}/plugin.json.
/// A failed plugin does not prevent the host or unrelated plugins from starting.
/// Loading a DLL executes trusted code in this process; this is NOT a sandbox.
/// </summary>
public static class DevDeckPluginLoader
{
    public sealed class PluginManifest
    {
        public string Id { get; set; } = "";
        public string Assembly { get; set; } = "";
    }

    private sealed class ActionPluginLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Keep host contracts in exactly one load context, avoiding duplicate type identities.
            Assembly shared = typeof(IDevDeckPlugin).Assembly;
            if (string.Equals(name.Name, shared.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return shared;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }

    public static (IReadOnlyList<IDevDeckOperation> Operations, IReadOnlyList<DevDeckPluginInfo> Plugins) Load(
        string baseDirectory, DevDeckProfiles profiles, IForegroundWindowReader foreground, DeckLog log,
        IEnumerable<string>? reservedActionIds = null,
        DevDeckPluginHostServices? hostServices = null)
    {
        var actions = new List<IDevDeckOperation>();
        var plugins = new List<DevDeckPluginInfo>();
        var known = new HashSet<string>(reservedActionIds ?? [], StringComparer.Ordinal);
        string pluginRoot = Path.Combine(baseDirectory, "plugins");
        if (!Directory.Exists(pluginRoot))
        {
            log.WriteWarning($"DevDeck action plugins directory missing: {pluginRoot}");
            return (actions, plugins);
        }

        foreach (string folder in Directory.GetDirectories(pluginRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            string manifestPath = Path.Combine(folder, "plugin.json");
            if (!File.Exists(manifestPath)) continue;
            string folderName = Path.GetFileName(folder);
            string id = folderName;
            try
            {
                var options = new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), options)
                    ?? throw new InvalidOperationException("Empty plugin.json.");
                if (string.IsNullOrWhiteSpace(manifest.Id) ||
                    string.IsNullOrWhiteSpace(manifest.Assembly) ||
                    Path.GetFileName(manifest.Assembly) != manifest.Assembly ||
                    !manifest.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Invalid plugin ID or DLL filename.");
                id = manifest.Id;
                string configPath = Path.Combine(folder, "config.json");
                if (!File.Exists(configPath))
                    throw new FileNotFoundException("Plugin configuration is missing.", configPath);
                using JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                if (config.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Plugin config.json must be a JSON object.");
                var schemaErrors = DevDeckPluginConfigurationStore.Validate(folder,
                    DevDeckPluginConfigurationStore.ParseObject(config.RootElement.GetRawText()));
                if (schemaErrors.Count != 0)
                    throw new InvalidOperationException("Invalid plugin configuration: " + string.Join("; ", schemaErrors));
                if (config.RootElement.TryGetProperty("Enabled", out JsonElement enabled) &&
                    enabled.ValueKind == JsonValueKind.False)
                {
                    plugins.Add(new DevDeckPluginInfo(id, id, folderName, 0, false, "Disabled in config.json"));
                    log.Write($"Action plugin disabled: {id}");
                    continue;
                }
                string path = Path.Combine(folder, manifest.Assembly);
                if (!File.Exists(path)) throw new FileNotFoundException("Plugin DLL not found.", path);
                var loadContext = new ActionPluginLoadContext(path);
                Assembly assembly = loadContext.LoadFromAssemblyPath(path);
                Type[] implementations = assembly.GetExportedTypes()
                    .Where(t => typeof(IDevDeckPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    .ToArray();
                if (implementations.Length != 1)
                    throw new InvalidOperationException("Exactly one public IDevDeckPlugin implementation is required.");
                if (Activator.CreateInstance(implementations[0]) is not IDevDeckPlugin plugin)
                    throw new InvalidOperationException("Plugin requires a public parameterless constructor.");
                if (!string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException("Plugin ID and manifest ID differ.");
                if (plugins.Any(p => p.Id == plugin.Id))
                    throw new InvalidOperationException("Duplicate plugin ID.");
                IReadOnlyList<IDevDeckOperation> produced = plugin.CreateOperations(
                    new DevDeckPluginContext(config.RootElement.Clone(), profiles, foreground, log, hostServices))
                    ?? throw new InvalidOperationException("Plugin returned a null action list.");
                if (produced.Any(a => a is null || string.IsNullOrWhiteSpace(a.PluginActionId)) ||
                    produced.Select(a => a.PluginActionId).Distinct(StringComparer.Ordinal).Count() != produced.Count ||
                    produced.Any(a => known.Contains(a.PluginActionId)))
                    throw new InvalidOperationException("Plugin contains an invalid or duplicate action ID.");
                foreach (var operation in produced)
                {
                    known.Add(operation.PluginActionId);
                    actions.Add(operation);
                }
                plugins.Add(new DevDeckPluginInfo(plugin.Id, plugin.Name, folderName, produced.Count, true));
                log.Write($"Loaded action plugin {plugin.Id} ({produced.Count} actions) from {folderName}.");
            }
            catch (Exception ex)
            {
                plugins.Add(new DevDeckPluginInfo(id, id, folderName, 0, false, ex.Message));
                log.WriteWarning($"Action plugin '{folderName}' failed to load: {ex}");
            }
        }
        return (actions, plugins);
    }
}
