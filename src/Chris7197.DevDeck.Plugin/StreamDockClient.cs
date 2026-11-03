using chris7197.DevDeck.Core;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace chris7197.DevDeck.Plugin;

/// <summary>A minimal typed boundary for Stream Dock's documented JSON/WebSocket wire protocol.</summary>
public sealed record StreamDockKeyEvent(string Action, string Context, DeckActionSettings? Settings = null);

public sealed class StreamDockClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action<Exception>? _eventError;

    public StreamDockClient(Action<Exception>? eventError = null) => _eventError = eventError;
    public event Func<StreamDockKeyEvent, Task>? KeyDown;
    public event Func<StreamDockKeyEvent, Task>? WillAppear;
    public event Func<StreamDockKeyEvent, Task>? WillDisappear;
    public event Func<StreamDockKeyEvent, Task>? SettingsChanged;

    public async Task RunAsync(int port, string pluginUuid, string registrationEvent, CancellationToken ct)
    {
        await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct);
        await SendAsync(new { @event = registrationEvent, uuid = pluginUuid }, ct);
        var bytes = new byte[8192];
        using var message = new MemoryStream();

        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await _socket.ReceiveAsync(new ArraySegment<byte>(bytes), ct);
                if (received.MessageType == WebSocketMessageType.Close) return;
                if (received.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("Expected a text WebSocket event.");
                message.Write(bytes, 0, received.Count);
                if (message.Length > 1024 * 1024) throw new InvalidOperationException("Stream Dock event exceeds 1 MB.");
            } while (!received.EndOfMessage);

            // Bad settings, unexpected events, and a failed handler are not socket failures.
            // Keep receiving subsequent button events rather than disconnecting the plugin.
            await DispatchSafelyAsync(message.ToArray(), ct);
        }
    }

    // Kept separate from the socket read loop for repeatable protocol fault-injection tests.
    internal async Task DispatchSafelyAsync(byte[] json, CancellationToken ct)
    {
        try { await DispatchAsync(json); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            try { _eventError?.Invoke(ex); } catch { /* Event logging must never break the socket. */ }
        }
    }

    private async Task DispatchAsync(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("event", out JsonElement eventProp) ||
            eventProp.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("context", out JsonElement contextProp) ||
            contextProp.ValueKind != JsonValueKind.String) return;
        string? type = eventProp.GetString();
        string? context = contextProp.GetString();
        string? action = root.TryGetProperty("action", out JsonElement actionProp) &&
            actionProp.ValueKind == JsonValueKind.String ? actionProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(action)) return;
        DeckActionSettings? settings = null;
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("settings", out var settingsJson))
            settings = DeckActionSettings.Parse(settingsJson);
        var keyEvent = new StreamDockKeyEvent(action, context, settings);
        Func<StreamDockKeyEvent, Task>? handler = type switch
        {
            "willAppear" => WillAppear,
            "willDisappear" => WillDisappear,
            "keyDown" => KeyDown,
            "didReceiveSettings" => SettingsChanged,
            _ => null
        };
        if (handler is null) return;
        foreach (Func<StreamDockKeyEvent, Task> subscriber in handler.GetInvocationList())
        {
            try { await subscriber(keyEvent); }
            catch (Exception ex)
            {
                try { _eventError?.Invoke(ex); } catch { }
            }
        }
    }

    public Task SetTitleAsync(string context, string title) => SendAsync(new
    {
        @event = "setTitle", context, payload = new { title, target = 0 }
    });

    public Task SetImageAsync(string context, string imageDataUrl) => SendAsync(new
    {
        @event = "setImage", context, payload = new { image = imageDataUrl, target = 0 }
    });

    public Task ShowOkAsync(string context) => SendAsync(new { @event = "showOk", context });
    public Task ShowAlertAsync(string context) => SendAsync(new { @event = "showAlert", context });

    private async Task SendAsync(object payload, CancellationToken ct = default)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(new ArraySegment<byte>(json), WebSocketMessageType.Text, true, ct);
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        await ValueTask.CompletedTask;
    }
}
