using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimLauncher.Core.Processes;

namespace SimLauncher.Core;

/// <summary>
/// Loopback-only HTTP endpoint so external controllers (Stream Deck, macro pads, scripts)
/// can read session status and start/stop the stack. Bound to 127.0.0.1 only, which needs
/// no URL ACL and is not reachable off the machine. Any local process can drive it — that
/// is the same trust level as clicking the tray icon, so there is no auth.
///
/// Routes (GET or POST, so a browser or curl works):
///   /status  -> JSON snapshot
///   /start   -> launch MSFS if needed + arm the session
///   /stop    -> teardown managed apps; ?msfs=1 also asks MSFS to close
///   /toggle  -> /start or /stop depending on current state
/// </summary>
public sealed class LocalApi : IDisposable
{
    private readonly SessionCoordinator _coordinator;
    private readonly IProcessManager _procs;
    private readonly ILogger<LocalApi> _log;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;

    public LocalApi(SessionCoordinator coordinator, IProcessManager procs, ILogger<LocalApi> log)
    {
        _coordinator = coordinator;
        _procs = procs;
        _log = log;
    }

    /// <summary>Starts listening. Port 0 in config disables the endpoint. Never throws.</summary>
    public void Start()
    {
        var port = _coordinator.Config.Current.LocalApiPort;
        if (port <= 0)
        {
            _log.LogInformation("Local API disabled (localApiPort = {Port})", port);
            return;
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _log.LogInformation("Local API listening on http://127.0.0.1:{Port}/", port);
            _ = Task.Run(AcceptLoopAsync);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Local API could not listen on port {Port}; external control unavailable", port);
            _listener = null;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || _listener is not { IsListening: true })
            {
                return; // shutting down
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Local API accept failed");
                continue;
            }

            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // The Stream Deck plugin page is a file:// origin, so it sends Origin: null.
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                await WriteAsync(ctx, 204, "").ConfigureAwait(false);
                return;
            }

            switch (ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant())
            {
                case "":
                case "/status":
                    await WriteAsync(ctx, 200, Status()).ConfigureAwait(false);
                    return;

                case "/start":
                    await _coordinator.LaunchMsfsAndStartSessionAsync().ConfigureAwait(false);
                    await WriteAsync(ctx, 200, Status()).ConfigureAwait(false);
                    return;

                case "/stop":
                    await _coordinator.StopSessionAsync().ConfigureAwait(false);
                    if (ctx.Request.QueryString["msfs"] == "1")
                    {
                        CloseMsfs();
                    }
                    await WriteAsync(ctx, 200, Status()).ConfigureAwait(false);
                    return;

                case "/toggle":
                    if (_coordinator.IsSessionActive)
                    {
                        await _coordinator.StopSessionAsync().ConfigureAwait(false);
                        if (ctx.Request.QueryString["msfs"] == "1")
                        {
                            CloseMsfs();
                        }
                    }
                    else
                    {
                        await _coordinator.LaunchMsfsAndStartSessionAsync().ConfigureAwait(false);
                    }
                    await WriteAsync(ctx, 200, Status()).ConfigureAwait(false);
                    return;

                default:
                    await WriteAsync(ctx, 404, """{"error":"unknown route"}""").ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Local API request failed");
            try
            {
                await WriteAsync(ctx, 500, """{"error":"failed"}""").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Client hung up mid-response; nothing to do.
            }
        }
    }

    /// <summary>
    /// Asks MSFS to close its main window. Deliberately never kills it: a forced kill on
    /// the sim risks a corrupt save/state, and the user can always alt-F4 themselves.
    /// </summary>
    private void CloseMsfs()
    {
        foreach (var name in _coordinator.Config.Current.Msfs.ProcessNames)
        {
            var proc = _procs.FindExisting(name);
            if (proc is null)
            {
                continue;
            }
            _log.LogInformation("Local API: asking MSFS ({Name}, pid {Pid}) to close", name, proc.Pid);
            if (!proc.TryCloseMainWindow())
            {
                _log.LogWarning("MSFS ({Name}) did not accept a close request; leaving it running", name);
            }
        }
    }

    private string Status()
    {
        var apps = _coordinator.Engine.Apps;
        return JsonSerializer.Serialize(new
        {
            simRunning = _coordinator.IsMsfsProcessRunning(),
            connected = _coordinator.IsSimConnected,
            sessionActive = _coordinator.IsSessionActive,
            phase = _coordinator.StateMachine.Phase.ToString(),
            profile = _coordinator.Config.Current.FindActiveProfile()?.Name ?? "",
            appsRunning = apps.Count(a => a.IsRunning),
            appsTotal = apps.Count,
            apps = apps.Select(a => new { name = a.Name, status = a.Status.ToString() }),
        });
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Close();
        _cts.Dispose();
    }
}
