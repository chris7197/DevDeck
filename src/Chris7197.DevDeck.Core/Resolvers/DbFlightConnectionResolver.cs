using System.Data.Common;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Selects an application configuration file using the full solution path.
/// ConfigPath is relative to the solution directory (or absolute if explicitly supplied).
/// First matching PathFilter wins; rules use the same '*' wildcard matching as migrations.
/// </summary>
public sealed class DbFlightRecorderConfigMapping
{
    public string PathFilter { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string ConnectionStringName { get; set; } = "";
    public bool CaptureSqlText { get; set; }
    public bool CaptureExecutionPlans { get; set; }
}

/// <summary>
/// Contains a secret-bearing connection string. Never serialize or log this record.
/// Only Source, Name, Server and Database are safe for status/previews/reports.
/// </summary>
public sealed record ResolvedDbFlightConnection(
    string Name, string Server, string Database, string ConnectionString,
    string Source, bool CaptureSqlText, bool CaptureExecutionPlans);

public sealed class DbFlightConnectionResolver(DeckSettings settings)
{
    public ResolvedDbFlightConnection Resolve(string? targetName, string? solutionPath)
    {
        // Explicit button target is always authoritative (backwards compatibility).
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            DbFlightRecorderTarget target = settings.DbFlightRecorderTargets.FirstOrDefault(x =>
                string.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException($"Unknown DB Flight target '{targetName}'. Check DbFlightRecorderTargets.");

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = target.Server,
                InitialCatalog = target.Database,
                IntegratedSecurity = true,
                TrustServerCertificate = target.TrustServerCertificate
            };
            ConfigureRecorderOptions(builder);
            return new(target.Name, builder.DataSource, builder.InitialCatalog, builder.ConnectionString,
                "Named target", target.CaptureSqlText, target.CaptureExecutionPlans);
        }

        if (string.IsNullOrWhiteSpace(solutionPath))
            throw new InvalidOperationException("Focus a Visual Studio solution or configure an explicit DB Flight target for this key.");

        string fullSolution = Path.GetFullPath(solutionPath);
        DbFlightRecorderConfigMapping mapping = settings.DbFlightRecorderConfigMappings.FirstOrDefault(x =>
            fullSolution.MatchesPattern(x.PathFilter)) ??
            throw new InvalidOperationException($"No DbFlightRecorderConfigMappings rule matches '{fullSolution}'. Configure a PathFilter/ConfigPath/ConnectionStringName, or set an explicit DB target.");

        string directory = Path.GetDirectoryName(fullSolution) ?? throw new InvalidOperationException("Solution path has no directory.");
        string configPath = Path.GetFullPath(Path.Combine(directory, mapping.ConfigPath));
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"DB Flight config was not found for rule '{mapping.PathFilter}'.", configPath);

        string value = UnwrapEntityConnectionString(ReadConnectionString(configPath, mapping.ConnectionStringName), configPath, mapping.ConnectionStringName);
        SqlConnectionStringBuilder connection;
        try
        {
            connection = new SqlConnectionStringBuilder(value);
        }
        catch (ArgumentException)
        {
            // Never echo the source value: it may contain database credentials.
            throw new InvalidOperationException($"'{mapping.ConnectionStringName}' in '{configPath}' isn't a supported SQL Server connection string. Check the provider and format.");
        }

        if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog))
            throw new InvalidOperationException($"'{mapping.ConnectionStringName}' in '{configPath}' must specify a SQL Server and database. No connection was attempted.");

        ConfigureRecorderOptions(connection);
        return new(Path.GetFileNameWithoutExtension(fullSolution), connection.DataSource, connection.InitialCatalog,
            connection.ConnectionString, $"{configPath} → {mapping.ConnectionStringName}",
            mapping.CaptureSqlText, mapping.CaptureExecutionPlans);
    }

    private static string UnwrapEntityConnectionString(string raw, string configPath, string name)
    {
        // Older EF6 EDMX applications can put a SqlClient connection string inside an
        // EntityClient string such as: metadata=...;provider=System.Data.SqlClient;
        // provider connection string="Data Source=...;Initial Catalog=...".
        if (!raw.Contains("provider connection string", StringComparison.OrdinalIgnoreCase)) return raw;
        try
        {
            var outer = new DbConnectionStringBuilder { ConnectionString = raw };
            if (!outer.TryGetValue("provider", out object? provider) ||
                !string.Equals(Convert.ToString(provider), "System.Data.SqlClient", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Connection string '{name}' in '{configPath}' does not use SQL Server EntityClient provider.");
            if (outer.TryGetValue("provider connection string", out object? inner) &&
                !string.IsNullOrWhiteSpace(Convert.ToString(inner)))
                return Convert.ToString(inner)!;
        }
        catch (ArgumentException)
        {
            // Deliberately omit the raw (possibly secret-bearing) connection string.
        }
        throw new InvalidOperationException($"'{name}' in '{configPath}' has an invalid EntityClient provider connection string.");
    }

    private static void ConfigureRecorderOptions(SqlConnectionStringBuilder connection)
    {
        // Preserve authentication, Encrypt and TrustServerCertificate from the application's config.
        // Override only recorder-specific operational properties.
        connection.ConnectTimeout = 5;
        connection.Pooling = false;
        connection.ApplicationName = "chris7197.DevDeck.FlightRecorder";
        connection.PersistSecurityInfo = false;
    }

    public static string ReadConnectionString(string path, string name)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return ReadJson(path, name);
        if (extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return ReadXml(path, name);
        throw new NotSupportedException($"Unsupported DB Flight config file type '{extension}'. Use appsettings.json, app.config or web.config.");
    }

    private static string ReadJson(string path, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Invalid JSON configuration in '{path}'.");
        if (TryProperty(doc.RootElement, "ConnectionStrings", out JsonElement section) &&
            section.ValueKind == JsonValueKind.Object && TryProperty(section, name, out JsonElement entry) &&
            entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            return entry.GetString()!;
        // Also tolerate a flattened configuration key, useful for generated settings files.
        if (TryProperty(doc.RootElement, $"ConnectionStrings:{name}", out JsonElement flat) &&
            flat.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(flat.GetString()))
            return flat.GetString()!;
        throw new InvalidOperationException($"Connection string '{name}' was not found under ConnectionStrings in '{path}'.");
    }

    private static bool TryProperty(JsonElement json, string key, out JsonElement property)
    {
        foreach (JsonProperty current in json.EnumerateObject())
            if (string.Equals(current.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                property = current.Value;
                return true;
            }
        property = default;
        return false;
    }

    private static string ReadXml(string path, string name)
    {
        XDocument document = LoadXml(path);
        XElement? section = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "connectionStrings");
        if (section is null)
            throw new InvalidOperationException($"No connectionStrings section in '{path}'.");
        string? external = (string?)section.Attribute("configSource");
        if (!string.IsNullOrWhiteSpace(external))
        {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string externalPath = Path.GetFullPath(Path.Combine(baseDir, external));
            string relative = Path.GetRelativePath(baseDir, externalPath);
            if (Path.IsPathRooted(external) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("connectionStrings configSource must reside within the config directory.");
            if (!File.Exists(externalPath))
                throw new FileNotFoundException("connectionStrings configSource file does not exist.", externalPath);
            XDocument nested = LoadXml(externalPath);
            section = nested.Root?.Name.LocalName == "connectionStrings" ? nested.Root :
                nested.Descendants().FirstOrDefault(x => x.Name.LocalName == "connectionStrings");
            if (section is null) throw new InvalidOperationException($"Missing connectionStrings section in '{externalPath}'.");
        }

        XElement? entry = section.Elements().FirstOrDefault(x => x.Name.LocalName == "add" &&
            string.Equals((string?)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
        string? value = (string?)entry?.Attribute("connectionString");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Connection string '{name}' wasn't found in '{path}'.");
        string? provider = (string?)entry?.Attribute("providerName");
        if (!string.IsNullOrWhiteSpace(provider) &&
            !provider.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("System.Data.EntityClient", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Connection string '{name}' uses '{provider}' rather than SQL Server SqlClient.");
        return value;
    }

    private static XDocument LoadXml(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader);
    }
}
