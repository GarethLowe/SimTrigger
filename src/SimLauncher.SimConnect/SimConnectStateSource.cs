using Microsoft.Extensions.Logging;
using SimLauncher.Core;
using SimLauncher.Core.Config;

namespace SimLauncher.SimConnect;

#if SIMCONNECT_SDK

using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using MsSimConnect = Microsoft.FlightSimulator.SimConnect.SimConnect;

/// <summary>
/// Real ISimStateSource backed by the MSFS SDK's managed SimConnect wrapper.
///
/// Connection strategy: poll every PollIntervalSeconds until the sim accepts the
/// connection — failures while the sim is not yet up are expected and never fatal.
/// All SimConnect calls are wrapped; a COMException degrades to ConnectionLost and
/// polling resumes, so a transient hiccup can reconnect within the grace period.
///
/// Uses the WaitHandle-based message pump (no window handle needed): SimConnect
/// signals the event, a background loop calls ReceiveMessage.
/// </summary>
public sealed class SimConnectStateSource : ISimStateSource
{
    private enum Definitions { CameraState, Telemetry }
    private enum Requests { CameraState, Telemetry }
    private enum Events { FlightLoaded, SimStart, Sim, Pause }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CameraData
    {
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    private struct TelemetryData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double IndicatedAirspeed;
    }

    private readonly ILogger<SimConnectStateSource> _log;
    private readonly Func<SimConnectionConfig> _configProvider;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private MsSimConnect? _sim;
    private EventWaitHandle? _signal;
    private volatile bool _connected;

    public event EventHandler<SimStateEvent>? StateEvent;
    public event EventHandler<SimTelemetry>? TelemetryReceived;

    public bool IsConnected => _connected;

    public SimConnectStateSource(ILogger<SimConnectStateSource> log, Func<SimConnectionConfig> configProvider)
    {
        _log = log;
        _configProvider = configProvider;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return Task.CompletedTask;
            }
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
            _loop = null;
        }
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Disconnect(emitLost: false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _log.LogInformation("SimConnect polling started");
        try
        {
            await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Backstop: this task is fire-and-forget, so a fault here (e.g. the
            // SimConnect assembly failing to load) must never disappear silently.
            _log.LogCritical(ex, "SimConnect polling loop crashed; sim state detection is dead");
        }
        _log.LogInformation("SimConnect polling stopped");
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (TryConnect())
            {
                Pump(ct); // blocks until disconnect/quit/cancel
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }
            var interval = TimeSpan.FromSeconds(Math.Max(1, _configProvider().PollIntervalSeconds));
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private bool TryConnect()
    {
        EventWaitHandle? signal = null;
        try
        {
            signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            var sim = new MsSimConnect("SimLauncher", IntPtr.Zero, 0, signal, 0);

            sim.OnRecvOpen += OnRecvOpen;
            sim.OnRecvQuit += OnRecvQuit;
            sim.OnRecvEvent += OnRecvEvent;
            sim.OnRecvEventFilename += OnRecvEventFilename;
            sim.OnRecvSimobjectData += OnRecvSimobjectData;
            sim.OnRecvException += OnRecvException;

            lock (_gate)
            {
                _sim = sim;
                _signal = signal;
            }
            _failedConnects = 0;
            return true;
        }
        catch (COMException)
        {
            // Expected until the sim is up and accepting connections.
            _log.LogDebug("SimConnect not available yet; will retry");
            ReleaseFailedAttempt(signal);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unexpected error creating SimConnect connection; will retry");
            ReleaseFailedAttempt(signal);
            return false;
        }
    }

    /// <summary>Failed connect attempts between forced finalizer drains. See <see cref="ReleaseFailedAttempt"/>.</summary>
    private const int OrphanDrainEvery = 10;

    private int _failedConnects;

    /// <summary>
    /// Reclaims what a failed connect attempt strands.
    ///
    /// The SDK's SimConnect constructor allocates its native connection state before
    /// SimConnect_Open fails and throws, so the half-built object never reaches us: it is
    /// unreachable and undisposable, and only its finalizer can free it. Its managed
    /// footprint is ~360 bytes, so while the sim is down the GC sees no pressure and never
    /// runs — every attempt then strands ~600 KB of native memory. Measured from a heap
    /// dump: 2,190 orphans (0 GC roots, 815 awaiting finalization) ≈ 1.3 GB after ~3 h
    /// idle, against a 38 MB managed heap.
    ///
    /// Forcing finalization is the only way to release a reference we do not hold. Doing it
    /// every Nth failure instead of every failure keeps the collections rare (~1/min at the
    /// default 5 s poll) while capping the strand at a few MB.
    /// </summary>
    private void ReleaseFailedAttempt(EventWaitHandle? signal)
    {
        signal?.Dispose(); // the ctor threw, so nothing native holds it
        if (++_failedConnects % OrphanDrainEvery != 0)
        {
            return;
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void Pump(CancellationToken ct)
    {
        var sim = _sim;
        var signal = _signal;
        if (sim is null || signal is null)
        {
            return;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (signal.WaitOne(500))
                {
                    sim.ReceiveMessage();
                }
            }
            Disconnect(emitLost: false);
        }
        catch (COMException ex)
        {
            _log.LogWarning("SimConnect connection dropped: {Message}", ex.Message);
            Disconnect(emitLost: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SimConnect message pump failed");
            Disconnect(emitLost: true);
        }
    }

    private void OnRecvOpen(MsSimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        _log.LogInformation("SimConnect connection accepted by {App} {Major}.{Minor}",
            data.szApplicationName, data.dwApplicationVersionMajor, data.dwApplicationVersionMinor);
        try
        {
            sender.SubscribeToSystemEvent(Events.FlightLoaded, "FlightLoaded");
            sender.SubscribeToSystemEvent(Events.SimStart, "SimStart");
            sender.SubscribeToSystemEvent(Events.Sim, "Sim");
            sender.SubscribeToSystemEvent(Events.Pause, "Pause");

            sender.AddToDataDefinition(Definitions.CameraState, "CAMERA STATE", "Enum",
                SIMCONNECT_DATATYPE.FLOAT64, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.RegisterDataDefineStruct<CameraData>(Definitions.CameraState);
            sender.RequestDataOnSimObject(Requests.CameraState, Definitions.CameraState,
                MsSimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);

            // Debug-panel telemetry: aircraft, position, altitude, speed (1 Hz).
            sender.AddToDataDefinition(Definitions.Telemetry, "TITLE", null,
                SIMCONNECT_DATATYPE.STRING256, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(Definitions.Telemetry, "PLANE LATITUDE", "degrees",
                SIMCONNECT_DATATYPE.FLOAT64, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(Definitions.Telemetry, "PLANE LONGITUDE", "degrees",
                SIMCONNECT_DATATYPE.FLOAT64, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(Definitions.Telemetry, "PLANE ALTITUDE", "feet",
                SIMCONNECT_DATATYPE.FLOAT64, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.AddToDataDefinition(Definitions.Telemetry, "AIRSPEED INDICATED", "knots",
                SIMCONNECT_DATATYPE.FLOAT64, 0f, MsSimConnect.SIMCONNECT_UNUSED);
            sender.RegisterDataDefineStruct<TelemetryData>(Definitions.Telemetry);
            sender.RequestDataOnSimObject(Requests.Telemetry, Definitions.Telemetry,
                MsSimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        }
        catch (COMException ex)
        {
            _log.LogError(ex, "Failed to subscribe to SimConnect events");
        }
        _connected = true;
        Emit(new SimStateEvent(SimStateEventKind.ConnectionOpened));
    }

    private void OnRecvQuit(MsSimConnect sender, SIMCONNECT_RECV data)
    {
        _log.LogInformation("SimConnect Quit received — sim is shutting down");
        _connected = false;
        Emit(new SimStateEvent(SimStateEventKind.Quit));
        Disconnect(emitLost: false); // Quit already tells the state machine everything
    }

    private void OnRecvEvent(MsSimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        switch ((Events)data.uEventID)
        {
            case Events.FlightLoaded:
                Emit(new SimStateEvent(SimStateEventKind.FlightLoaded));
                break;
            case Events.SimStart:
                Emit(new SimStateEvent(SimStateEventKind.SimStart));
                break;
            case Events.Sim:
                Emit(new SimStateEvent(SimStateEventKind.SimRunning, data.dwData));
                break;
            case Events.Pause:
                Emit(new SimStateEvent(SimStateEventKind.Pause, data.dwData));
                break;
        }
    }

    private void OnRecvEventFilename(MsSimConnect sender, SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        // "FlightLoaded" is a filename event; some SDK versions surface it here
        // instead of OnRecvEvent.
        if ((Events)data.uEventID == Events.FlightLoaded)
        {
            _log.LogDebug("FlightLoaded: {File}", data.szFileName);
            Emit(new SimStateEvent(SimStateEventKind.FlightLoaded));
        }
    }

    private void OnRecvSimobjectData(MsSimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwData is not { Length: > 0 })
        {
            return;
        }
        switch ((Requests)data.dwRequestID)
        {
            case Requests.CameraState when data.dwData[0] is CameraData camera:
                Emit(new SimStateEvent(SimStateEventKind.CameraState, camera.Value));
                break;

            case Requests.Telemetry when data.dwData[0] is TelemetryData t:
                try
                {
                    TelemetryReceived?.Invoke(this, new SimTelemetry(
                        t.Title ?? "", t.Latitude, t.Longitude, t.Altitude, t.IndicatedAirspeed));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Telemetry handler threw");
                }
                break;
        }
    }

    private void OnRecvException(MsSimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        _log.LogWarning("SimConnect exception: {Code}", (SIMCONNECT_EXCEPTION)data.dwException);
    }

    private void Disconnect(bool emitLost)
    {
        MsSimConnect? sim;
        EventWaitHandle? signal;
        lock (_gate)
        {
            sim = _sim;
            signal = _signal;
            _sim = null;
            _signal = null;
        }
        var wasConnected = _connected;
        _connected = false;
        try
        {
            sim?.Dispose();
        }
        catch
        {
            // Disposal after a dropped connection can throw; nothing to do.
        }
        signal?.Dispose();
        if (emitLost && wasConnected)
        {
            Emit(new SimStateEvent(SimStateEventKind.ConnectionLost));
        }
    }

    private void Emit(SimStateEvent e)
    {
        try
        {
            StateEvent?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "StateEvent handler threw for {Event}", e);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Disconnect(emitLost: false);
        _cts?.Dispose();
    }
}

#else

/// <summary>
/// Compile-time stub used when the MSFS SDK was not found during build.
/// The app runs, but sim state detection is disabled and the user is told why.
/// </summary>
public sealed class SimConnectStateSource : ISimStateSource
{
    private readonly ILogger<SimConnectStateSource> _log;

    public event EventHandler<SimStateEvent>? StateEvent;
    public event EventHandler<SimTelemetry>? TelemetryReceived;

    public bool IsConnected => false;

    public SimConnectStateSource(ILogger<SimConnectStateSource> log, Func<SimConnectionConfig> configProvider)
    {
        _log = log;
        _ = configProvider;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _log.LogError(
            "This build has no SimConnect support: the MSFS SDK was not found when SimLauncher was compiled. " +
            "Install the MSFS SDK, set MSFS_SDK/MSFS2024_SDK, and rebuild. See README ('SimConnect SDK setup').");
        StateEvent?.Invoke(this, new SimStateEvent(SimStateEventKind.ConnectionLost));
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public void Dispose() { }
}

#endif
