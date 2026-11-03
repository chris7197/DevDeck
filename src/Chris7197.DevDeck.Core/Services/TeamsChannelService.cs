using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace chris7197.DevDeck.Core;

/// <summary>
/// Uses delegated Graph channel-message permissions. A channel list does not expose the
/// Teams client's per-user unread badge: this service tracks NEW posts since the key was
/// first observed / last pressed, locally. No tokens are written to settings.json.
/// </summary>
public sealed class TeamsChannelService(TeamsAuthSettings settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiry;
    private readonly Dictionary<string, ChannelSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "chris7197", "DevDeck", "teams-seen.json");
    private readonly object _seenLock = new();
    private Dictionary<string, DateTimeOffset>? _seen;

    private string TokenKey => $"chris7197.DevDeck.Teams.{settings.TenantId}.{settings.ClientId}";
    private string Authority => $"https://login.microsoftonline.com/{Uri.EscapeDataString(settings.TenantId)}/oauth2/v2.0";
    private const string Scopes = "offline_access ChannelMessage.Read.All";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.ClientId) && !string.IsNullOrWhiteSpace(settings.TenantId);

    public async Task SignInAsync(Action<string> display, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["scope"] = Scopes
        });
        using var response = await Http.PostAsync(Authority + "/devicecode", request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Device-code request failed: " + GraphError(json));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        display(root.GetProperty("message").GetString() ?? "Open https://microsoft.com/devicelogin and enter your device code.");
        string deviceCode = root.GetProperty("device_code").GetString()!;
        int interval = root.TryGetProperty("interval", out var delay) ? delay.GetInt32() : 5;
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var tokenForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode
            });
            using var tokenResponse = await Http.PostAsync(Authority + "/token", tokenForm, cancellationToken);
            string body = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
            if (tokenResponse.IsSuccessStatusCode)
            {
                SaveToken(body);
                display("Microsoft Teams sign-in completed. Channel buttons can now refresh.");
                return;
            }
            using var errorDoc = JsonDocument.Parse(body);
            string error = errorDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            if (error == "authorization_pending") continue;
            if (error == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException("Teams sign-in failed: " + GraphError(body));
        }
        throw new TimeoutException("The Teams sign-in code expired; run --teams-login again.");
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException(
            "Set Teams.TenantId and Teams.ClientId in settings.json, then run the CLI --teams-login command.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        EnsureConfigured();
        await _authLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiry.AddMinutes(-2)) return _accessToken;
            string? refresh = WindowsCredentialStore.Read(TokenKey);
            if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("Teams not signed in. Run: DevDeck.Cli --teams-login");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId,
                ["scope"] = Scopes,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh
            });
            using var response = await Http.PostAsync(Authority + "/token", content, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Teams token refresh failed. Try --teams-login again: " + GraphError(body));
            SaveToken(body);
            return _accessToken!;
        }
        finally { _authLock.Release(); }
    }

    private void SaveToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        _accessToken = root.GetProperty("access_token").GetString()!;
        _expiry = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32());
        if (root.TryGetProperty("refresh_token", out var refresh) && !string.IsNullOrWhiteSpace(refresh.GetString()))
            WindowsCredentialStore.Write(TokenKey, refresh.GetString()!);
    }

    private static string GraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var desc) && desc.ValueKind == JsonValueKind.String)
                return desc.GetString()!.Split('\n')[0];
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString()!;
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var message))
                    return message.GetString() ?? "Graph error";
            }
        }
        catch (JsonException) { }
        return "HTTP request failed (details unavailable)";
    }

    public async Task<ChannelSnapshot> GetSnapshotAsync(DeckActionSettings options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.TeamId) || string.IsNullOrWhiteSpace(options.ChannelId))
            throw new InvalidOperationException("Configure Team ID and Channel ID on this button.");
        string key = ChannelKey(options);
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedUtc <
                TimeSpan.FromSeconds(settings.PollSeconds)) return cached;
        }
        finally { _cacheLock.Release(); }

        string token = await GetAccessTokenAsync(ct);
        string url = "https://graph.microsoft.com/v1.0/teams/" + Uri.EscapeDataString(options.TeamId) +
            "/channels/" + Uri.EscapeDataString(options.ChannelId) + "/messages?$top=30&$expand=replies";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Teams channel query failed ({(int)response.StatusCode}): {GraphError(body)}");
        using var doc = JsonDocument.Parse(body);
        var messages = new List<TeamsMessage>();
        if (doc.RootElement.TryGetProperty("value", out var values))
        foreach (var item in values.EnumerateArray())
        {
            AddMessage(item, messages);
            if (item.TryGetProperty("replies", out var replies) && replies.ValueKind == JsonValueKind.Array)
                foreach (var reply in replies.EnumerateArray()) AddMessage(reply, messages);
        }
        // Covers root posts plus up to 200 expanded replies per thread, not a full channel history.
        messages.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        var snapshot = new ChannelSnapshot(DateTimeOffset.UtcNow, messages);
        await _cacheLock.WaitAsync(ct);
        try { _cache[key] = snapshot; }
        finally { _cacheLock.Release(); }
        return snapshot;
    }

    private static void AddMessage(JsonElement item, List<TeamsMessage> messages)
    {
        if (!item.TryGetProperty("createdDateTime", out var created) ||
            !DateTimeOffset.TryParse(created.GetString(), out var timestamp)) return;
        string name = "Unknown";
        if (item.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object)
        foreach (string type in new[] { "user", "application" })
            if (from.TryGetProperty(type, out var person) && person.ValueKind == JsonValueKind.Object &&
                person.TryGetProperty("displayName", out var display) && !string.IsNullOrWhiteSpace(display.GetString()))
            { name = display.GetString()!; break; }
        string webUrl = item.TryGetProperty("webUrl", out var link) && link.ValueKind == JsonValueKind.String
            ? link.GetString() ?? "" : "";
        messages.Add(new TeamsMessage(timestamp, name, webUrl));
    }

    public static string Initials(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
    }

    public DevDeckButtonState Describe(DeckActionSettings options, ChannelSnapshot snapshot)
    {
        string key = ChannelKey(options);
        TeamsMessage? latest = snapshot.Messages.FirstOrDefault();
        DateTimeOffset seen = GetOrInitialiseSeen(key, latest?.CreatedUtc);
        int newCount = snapshot.Messages.Count(m => m.CreatedUtc > seen);
        string label = string.IsNullOrWhiteSpace(options.Label) ? "Teams" : options.Label;
        string who = latest is null ? "—" : Initials(latest.Author);
        return new DevDeckButtonState(label, $"{newCount} new · {who}",
            newCount > 0 ? DeckStatus.Warning : DeckStatus.Success,
            $"Locally tracked new messages (not the Teams unread badge).\n" +
            $"Latest: {latest?.Author ?? "None"}\n" +
            $"Recent messages sampled: {snapshot.Messages.Count}; older messages may be omitted.",
            IconName: DevDeckIcons.TeamsChannel);
    }

    public async Task AcknowledgeAsync(DeckActionSettings options, CancellationToken ct = default)
    {
        ChannelSnapshot snapshot = await GetSnapshotAsync(options, ct);
        SetSeen(ChannelKey(options), snapshot.Messages.FirstOrDefault()?.CreatedUtc ?? DateTimeOffset.UtcNow);
    }

    public async Task<string> GetChannelUrlAsync(DeckActionSettings options, CancellationToken ct = default)
    {
        ChannelSnapshot snapshot = await GetSnapshotAsync(options, ct);
        if (!string.IsNullOrWhiteSpace(options.ChannelUrl) &&
            Uri.TryCreate(options.ChannelUrl, UriKind.Absolute, out var configured) && configured.Scheme == Uri.UriSchemeHttps)
            return options.ChannelUrl;
        string? web = snapshot.Messages.FirstOrDefault(m => Uri.TryCreate(m.WebUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)?.WebUrl;
        if (!string.IsNullOrWhiteSpace(web)) return web;
        throw new InvalidOperationException("No Teams message link was returned. Set ChannelUrl for this key.");
    }

    public int GetNewCount(DeckActionSettings options, ChannelSnapshot snapshot)
    {
        var seen = GetOrInitialiseSeen(ChannelKey(options), snapshot.Messages.FirstOrDefault()?.CreatedUtc);
        return snapshot.Messages.Count(m => m.CreatedUtc > seen);
    }

    private static string ChannelKey(DeckActionSettings options) => options.TeamId + "/" + options.ChannelId;

    private DateTimeOffset GetOrInitialiseSeen(string key, DateTimeOffset? latest)
    {
        lock (_seenLock)
        {
            LoadSeen();
            if (_seen!.TryGetValue(key, out var date)) return date;
            date = latest ?? DateTimeOffset.UtcNow;
            _seen[key] = date; // Initial baseline: no artificial "30 unread" on first launch.
            PersistSeen();
            return date;
        }
    }

    private void SetSeen(string key, DateTimeOffset seen)
    {
        lock (_seenLock)
        {
            LoadSeen();
            _seen![key] = seen;
            PersistSeen();
        }
    }

    private void LoadSeen()
    {
        if (_seen is not null) return;
        try { _seen = File.Exists(_statePath)
            ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_statePath)) ?? new()
            : new(); }
        catch (JsonException) { _seen = new(); }
    }

    private void PersistSeen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_seen, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record TeamsMessage(DateTimeOffset CreatedUtc, string Author, string WebUrl);
public sealed record ChannelSnapshot(DateTimeOffset FetchedUtc, IReadOnlyList<TeamsMessage> Messages);

/// <summary>Refresh token stored per Windows user in Credential Manager (not in the ZIP, config or logs).</summary>
internal static class WindowsCredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);

    public static string? Read(string key)
    {
        if (!CredRead(key, 1, 0, out IntPtr ptr)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(ptr);
            return credential.CredentialBlobSize == 0 ? null :
                Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string key, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > 2500) throw new InvalidOperationException("Refresh token exceeds Windows Credential Manager limits.");
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new NativeCredential
            {
                Type = 1, TargetName = key, CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length, Persist = 2, UserName = Environment.UserName
            };
            if (!CredWrite(ref cred, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(blob); }
    }
}
