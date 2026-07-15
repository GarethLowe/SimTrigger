using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SimLauncher.Traffic;

/// <summary>
/// Transport only: maintains the loopback WebSocket to BeyondATC's traffic server and
/// hands raw JSON text up. The server only listens during an active flight, so the
/// client retries forever and treats every failure as "not up yet".
/// </summary>
public sealed class TrafficWebSocketClient : IAsyncDisposable, IDisposable
{
    public static readonly Uri DefaultUri = new("ws://127.0.0.1:41717/");

    private readonly Uri _uri;
    private readonly ILogger _log;
    private readonly TimeSpan _reconnectDelay;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Task? _runLoop;
    private ClientWebSocket? _socket;
    private volatile bool _connected;

    /// <summary>Raw JSON text of every server message. Raised on a background thread.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>True on connect, false on loss. Raised on a background thread.</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _connected;

    public TrafficWebSocketClient(ILogger log, Uri? uri = null, TimeSpan? reconnectDelay = null)
    {
        _log = log;
        _uri = uri ?? DefaultUri;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2.5);
    }

    public void Start()
    {
        _runLoop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.ConnectAsync(_uri, connectTimeout.Token);

                _socket = socket;
                _connected = true;
                _log.LogInformation("Traffic link connected to {Uri}", _uri);
                ConnectionChanged?.Invoke(true);

                await ReceiveLoopAsync(socket, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_connected)
                {
                    _log.LogWarning("Traffic link lost: {Message}", ex.Message);
                }
            }
            finally
            {
                _socket = null;
                if (_connected)
                {
                    _connected = false;
                    ConnectionChanged?.Invoke(false);
                }
            }

            try
            {
                await Task.Delay(_reconnectDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _log.LogInformation("Traffic server closed the connection");
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try
            {
                MessageReceived?.Invoke(text);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Traffic message handler threw; message dropped");
            }
        }
    }

    /// <summary>Sends one text frame. Returns false when the link is down; the caller surfaces it.</summary>
    public async Task<bool> SendAsync(string json)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return false;
        }
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Traffic send failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runLoop is not null)
        {
            try
            {
                await _runLoop;
            }
            catch
            {
                // Shutdown path; the loop already logged anything interesting.
            }
        }
        _cts.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>Sync teardown for DI container disposal: cancel and let the loop wind down on its own.</summary>
    public void Dispose()
    {
        _cts.Cancel();
    }
}
