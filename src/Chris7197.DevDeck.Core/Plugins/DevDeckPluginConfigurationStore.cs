using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Edits plugin config files without loading executable code. Schemas are optional metadata
/// supplied by each plugin; unlisted properties are deliberately preserved on save.
/// </summary>
public sealed class DevDeckPluginConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true,
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true };

    public sealed record SettingField(string Key, string Label, string Type, string Description = "",
        bool Nullable = false, decimal? Minimum = null, decimal? Maximum = null, string? Format = null);

    public sealed record Document(string PluginFolder, string ConfigPath, string ContentHash,
        JsonObject Values, IReadOnlyList<SettingField> Fields);

    public static Document Open(string pluginFolder)
    {
        string folder = Path.GetFullPath(pluginFolder);
        string path = Path.Combine(folder, "config.json");
        string content = File.ReadAllText(path);
        JsonObject values = ParseObject(content);
        var fields = ReadFields(folder, values);
        return new Document(folder, path, Hash(content), values, fields);
    }

    public static IReadOnlyList<string> Validate(string pluginFolder, JsonObject values)
    {
        var errors = new List<string>();
        IReadOnlyList<SettingField> fields;
        try { fields = ReadFields(pluginFolder, values); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        { return ["Cannot load settings schema: " + e.Message]; }

        foreach (SettingField field in fields)
        {
            if (!values.TryGetPropertyValue(field.Key, out JsonNode? node))
            {
                errors.Add($"{field.Label}: setting is missing.");
                continue;
            }
            if (node is null)
            {
                if (!field.Nullable) errors.Add($"{field.Label}: value cannot be null.");
                continue;
            }
            JsonElement raw;
            try { raw = JsonSerializer.SerializeToElement(node); }
            catch (Exception e) { errors.Add($"{field.Label}: {e.Message}"); continue; }
            bool valid = field.Type switch
            {
                "boolean" => raw.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out _),
                "number" => raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out _),
                "string" => raw.ValueKind == JsonValueKind.String,
                "array" => raw.ValueKind == JsonValueKind.Array,
                "object" => raw.ValueKind == JsonValueKind.Object,
                "json" => true,
                _ => false
            };
            if (!valid)
            {
                errors.Add($"{field.Label}: expected {field.Type}.");
                continue;
            }
            if ((field.Type is "integer" or "number") && raw.TryGetDecimal(out decimal number))
            {
                if (field.Minimum.HasValue && number < field.Minimum.Value)
                    errors.Add($"{field.Label}: minimum is {field.Minimum}.");
                if (field.Maximum.HasValue && number > field.Maximum.Value)
                    errors.Add($"{field.Label}: maximum is {field.Maximum}.");
            }
            if (field.Type == "string" && field.Format == "regex")
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(raw.GetString())) throw new ArgumentException("Regex cannot be blank.");
                    _ = new Regex(raw.GetString()!, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException) { errors.Add($"{field.Label}: invalid regular expression."); }
            }
            if (field.Type == "string" && field.Format == "file-pattern")
            {
                string value = raw.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '\\']) >= 0)
                    errors.Add($"{field.Label}: use a filename pattern, not a path.");
            }
        }
        return errors;
    }

    /// <summary>
    /// Optimistic conflict detection prevents trampling external/manual edits. A backup is
    /// retained and an atomic replace is used when a previous config exists.
    /// </summary>
    public static Document Save(Document original, JsonObject proposed)
    {
        string path = original.ConfigPath;
        string folder = Path.GetDirectoryName(path)!;
        if (!Path.GetFullPath(folder).Equals(original.PluginFolder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The plugin config path changed.");
        var errors = Validate(folder, proposed);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        string current = File.ReadAllText(path);
        if (!string.Equals(Hash(current), original.ContentHash, StringComparison.Ordinal))
            throw new IOException("Configuration was changed by another process. Refresh the plugin before saving.");
        string data = proposed.ToJsonString(JsonOptions) + Environment.NewLine;
        if (string.Equals(data, current, StringComparison.Ordinal)) return Open(folder);
        string temporary = Path.Combine(folder, ".config-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = path + ".bak";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(data);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            // config.json is required by the loader and is therefore always an existing file.
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Open(folder);
    }

    public static JsonObject ParseObject(string json)
    {
        JsonNode? parsed = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return parsed as JsonObject ?? throw new JsonException("Configuration must be a JSON object.");
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static IReadOnlyList<SettingField> ReadFields(string folder, JsonObject values)
    {
        string schemaPath = Path.Combine(folder, "config.schema.json");
        var result = new List<SettingField>();
        JsonObject? schema = File.Exists(schemaPath)
            ? ParseObject(File.ReadAllText(schemaPath)) : null;
        JsonObject? configured = schema?["fields"] as JsonObject;
        if (schema is not null && configured is null)
            throw new JsonException("Settings schema must contain a fields object.");

        foreach (var property in values)
        {
            JsonObject? spec = configured?[property.Key] as JsonObject;
            string type = ReadString(spec, "type") ?? InferType(property.Value);
            result.Add(new SettingField(property.Key,
                ReadString(spec, "label") ?? property.Key,
                type,
                ReadString(spec, "description") ?? "",
                ReadBoolean(spec, "nullable") ?? (property.Value is null),
                ReadDecimal(spec, "minimum"), ReadDecimal(spec, "maximum"),
                ReadString(spec, "format")));
        }
        if (configured is not null)
            foreach (var spec in configured)
                if (!values.ContainsKey(spec.Key))
                    throw new JsonException($"Configuration is missing schema field '{spec.Key}'.");
        return result;
    }

    private static string InferType(JsonNode? value)
    {
        if (value is JsonArray) return "array";
        if (value is JsonObject) return "object";
        if (value is null) return "json";
        JsonElement e = JsonSerializer.SerializeToElement(value);
        return e.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => "number",
            _ => "json"
        };
    }
    private static string? ReadString(JsonObject? obj, string key) => obj?[key]?.GetValue<string>();
    private static bool? ReadBoolean(JsonObject? obj, string key) => obj?[key]?.GetValue<bool>();
    private static decimal? ReadDecimal(JsonObject? obj, string key) => obj?[key]?.GetValue<decimal>();
}
