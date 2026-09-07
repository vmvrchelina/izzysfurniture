using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IzzysFurniture;

internal sealed class SyncRelayClient : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private string _status = "Sync disconnected.";
    private string? _pendingSceneJson;
    private string? _lastSentSceneJson;

    public string Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _socket?.State == WebSocketState.Open;
        }
    }

    public async Task ConnectAsync(string relayUrl, string room, string secret, bool host)
    {
        Disconnect();

        var uri = new Uri(relayUrl.Trim());
        room = room.Trim();
        if (room.Length == 0)
        {
            SetStatus("enter a room name");
            return;
        }

        var ws = new ClientWebSocket();
        var cts = new CancellationTokenSource();

        lock (_gate)
        {
            // publish the new connection as one state change for the ui thread
            _socket = ws;
            _cancellation = cts;
            _lastSentSceneJson = null;
            _status = $"Connecting to {uri.Host}...";
        }

        var ready = false;
        try
        {
            await ws.ConnectAsync(uri, cts.Token).ConfigureAwait(false);

            lock (_gate)
            {
                // a newer connection may have replaced this one while connect was pending
                if (!ReferenceEquals(_socket, ws))
                    return;

                _status = $"Connected to {uri.Host}; joining room...";
            }

            if (!await SendRawAsync(new
            {
                type = "join",
                room,
                role = host ? "host" : "viewer",
                secret,
            }).ConfigureAwait(false))
            {
                return;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_socket, ws))
                    return;

                _receiveTask = ReceiveLoop(ws, cts.Token);
                ready = true;
            }
        }
        finally
        {
            if (!ready)
                ReleaseConnection(ws, cts);
        }
    }

    public void Disconnect()
    {
        ClientWebSocket? ws;
        CancellationTokenSource? cts;
        Task? receiver;
        lock (_gate)
        {
            ws = _socket;
            cts = _cancellation;
            receiver = _receiveTask;
            _socket = null;
            _cancellation = null;
            _receiveTask = null;
            _status = "Sync disconnected.";
        }

        cts?.Cancel();
        receiver?.GetAwaiter().GetResult();
        ws?.Dispose();
        cts?.Dispose();
    }

    public async Task SendSceneSnapshotAsync(string sceneJson)
    {
        lock (_gate)
        {
            if (string.Equals(sceneJson, _lastSentSceneJson, StringComparison.Ordinal))
                return;
        }

        var sent = await SendRawAsync(new
        {
            type = "scene_snapshot",
            sceneJson,
        }).ConfigureAwait(false);

        if (!sent)
            return;

        lock (_gate)
        {
            _lastSentSceneJson = sceneJson;
            _status = $"Sent scene snapshot ({sceneJson.Length / 1024.0f:N1} KB).";
        }
    }

    public bool TryTakePendingScene(out string sceneJson)
    {
        lock (_gate)
        {
            if (_pendingSceneJson == null)
            {
                sceneJson = string.Empty;
                return false;
            }

            sceneJson = _pendingSceneJson;
            _pendingSceneJson = null;
            return true;
        }
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }

    public void SetError(string message)
    {
        SetStatus(message);
    }

    private async Task<bool> SendRawAsync<T>(T payload)
    {
        ClientWebSocket? ws;
        CancellationToken token;
        lock (_gate)
        {
            ws = _socket;
            if (ws == null || ws.State != WebSocketState.Open || _cancellation == null)
            {
                _status = "not connected";
                return false;
            }

            token = _cancellation.Token;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        // clientwebsocket supports one concurrent send at a time
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetStatus("Sync relay closed the connection.");
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                // websocket fragments are byte boundaries
                // decoding only after the final fragment keeps split utf-8 characters intact
                HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            Service.Log.Warning("sync connection dropped");
            SetStatus("sync connection dropped");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (string.Equals(type, "joined", StringComparison.OrdinalIgnoreCase))
            {
                var room = root.GetProperty("room").GetString();
                var role = root.GetProperty("role").GetString();
                SetStatus($"Sync joined room {room} as {role}.");
                return;
            }

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("relay reported an error");
                return;
            }

            if (string.Equals(type, "scene_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                var sceneJson = root.GetProperty("sceneJson").GetString();
                if (string.IsNullOrWhiteSpace(sceneJson))
                    return;

                lock (_gate)
                {
                    // sync is too much for this honestly
                    _pendingSceneJson = sceneJson;
                    _status = $"Received scene snapshot ({sceneJson.Length / 1024.0f:N1} KB).";
                }
            }
        }
        catch (JsonException)
        {
            Service.Log.Warning("relay sent invalid json");
        }
    }

    private void SetStatus(string value)
    {
        lock (_gate)
            _status = value;
    }

    private void ReleaseConnection(ClientWebSocket ws, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_socket, ws))
                return;

            _socket = null;
            _cancellation = null;
            _receiveTask = null;
        }

        cts.Cancel();
        ws.Dispose();
        cts.Dispose();
    }
}
