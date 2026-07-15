using Microsoft.Extensions.Logging;
using SimLauncher.Core.Config;

namespace SimLauncher.Core;

public enum SessionPhase
{
    Idle,
    WaitingForSim,
    SimRunning,
    WorldLoaded,
    InCockpit,
    InMenu,
    Ended,
}

/// <summary>
/// Turns raw <see cref="SimStateEvent"/>s into checkpoint firings.
///
/// Rules implemented here:
///  - OnSimStart and OnSimExit fire once per session.
///  - OnWorldLoad requires the FlightLoaded system event, gated on CAMERA STATE having
///    left the menu/loading values (MSFS fires FlightLoaded during menu transitions).
///  - OnEnterCockpit fires when the debounced camera state enters the cockpit range.
///  - OnExitFlight fires when the camera returns to the main menu while connected, and
///    re-arms OnWorldLoad/OnEnterCockpit for the next flight (re-entrancy).
///  - Camera transitions are debounced (default 2 s) so flicker cannot double-fire.
///  - Connection loss starts a grace timer (default 30 s); reconnection within it is a
///    transient hiccup, expiry (or the Quit event) means the sim is gone → OnSimExit.
///
/// All timing goes through <see cref="TimeProvider"/> so tests can drive a fake clock.
/// </summary>
public sealed class SessionStateMachine : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<SessionStateMachine> _log;

    private SimConnectionConfig _cfg = new();

    private bool _armed;
    private bool _simStartFired;
    private bool _simExitFired;
    private bool _worldLoadFired;
    private bool _cockpitFired;
    private bool _flightLoadedPending;
    private bool _connected;

    private double? _committedCamera;
    private double? _pendingCamera;
    private ITimer? _debounceTimer;
    private ITimer? _graceTimer;

    public SessionPhase Phase { get; private set; } = SessionPhase.Idle;
    public double? CameraState => _committedCamera;
    public bool IsSessionActive => _armed;

    /// <summary>Fired when a checkpoint is reached. Fired outside the internal lock.</summary>
    public event Action<Checkpoint>? CheckpointReached;
    public event Action<SessionPhase>? PhaseChanged;

    public SessionStateMachine(ILogger<SessionStateMachine> log, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public void Configure(SimConnectionConfig cfg)
    {
        lock (_gate)
        {
            _cfg = cfg;
        }
    }

    /// <summary>Arms the session and fires LauncherStart (MSFS + alongside apps).</summary>
    public void StartSession()
    {
        List<Checkpoint> fired = new();
        lock (_gate)
        {
            if (_armed)
            {
                _log.LogWarning("StartSession ignored: session already active");
                return;
            }
            _armed = true;
            _simStartFired = false;
            _simExitFired = false;
            _worldLoadFired = false;
            _cockpitFired = false;
            _flightLoadedPending = false;
            SetPhase(SessionPhase.WaitingForSim);
            fired.Add(Checkpoint.LauncherStart);

            // The sim may already be up (connection established before arming).
            if (_connected)
            {
                _simStartFired = true;
                SetPhase(SessionPhase.SimRunning);
                fired.Add(Checkpoint.OnSimStart);

                // Attaching to a sim that is already mid-flight (auto-arm while flying):
                // catch the timeline up from the current camera state.
                if (_committedCamera is double cam && _cfg.CameraStates.IsInFlight(cam))
                {
                    _worldLoadFired = true;
                    SetPhase(SessionPhase.WorldLoaded);
                    fired.Add(Checkpoint.OnWorldLoad);
                    if (_cfg.CameraStates.IsCockpit(cam))
                    {
                        _cockpitFired = true;
                        SetPhase(SessionPhase.InCockpit);
                        fired.Add(Checkpoint.OnEnterCockpit);
                    }
                }
            }
        }
        Emit(fired);
    }

    /// <summary>Manual stop: behaves like a sim exit (teardown).</summary>
    public void StopSession()
    {
        FireSimExit("manual stop");
    }

    public void Handle(SimStateEvent e)
    {
        var quit = false;
        List<Checkpoint>? fired = null;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case SimStateEventKind.ConnectionOpened:
                    _connected = true;
                    if (_graceTimer is not null)
                    {
                        _log.LogInformation("SimConnect reconnected within grace period; session continues");
                        _graceTimer.Dispose();
                        _graceTimer = null;
                    }
                    if (_armed && !_simStartFired)
                    {
                        _simStartFired = true;
                        SetPhase(SessionPhase.SimRunning);
                        (fired ??= new()).Add(Checkpoint.OnSimStart);
                    }
                    break;

                case SimStateEventKind.ConnectionLost:
                    _connected = false;
                    _committedCamera = null;
                    _pendingCamera = null;
                    _debounceTimer?.Dispose();
                    _debounceTimer = null;
                    if (_armed && !_simExitFired && _graceTimer is null)
                    {
                        var grace = TimeSpan.FromSeconds(_cfg.DisconnectGraceSeconds);
                        _log.LogWarning("SimConnect connection lost; teardown in {Grace}s unless it recovers", grace.TotalSeconds);
                        _graceTimer = _time.CreateTimer(
                            _ => FireSimExit("connection lost and not re-established within grace period"),
                            null, grace, Timeout.InfiniteTimeSpan);
                    }
                    break;

                case SimStateEventKind.Quit:
                    _connected = false;
                    quit = true;
                    break;

                case SimStateEventKind.FlightLoaded:
                    if (!_armed || _simExitFired)
                    {
                        break;
                    }
                    if (_committedCamera is double cam && !_cfg.CameraStates.IsMenuOrLoading(cam))
                    {
                        fired = FireWorldLoadLocked(fired);
                    }
                    else
                    {
                        // Camera still in menu/loading (or unknown): hold until it commits
                        // to an in-flight value. Guards against the menu-transition quirk.
                        _log.LogDebug("FlightLoaded received while camera={Camera}; gating on camera state", _committedCamera);
                        _flightLoadedPending = true;
                    }
                    break;

                case SimStateEventKind.CameraState:
                    HandleCameraLocked(e.Value);
                    break;

                // Informational only; surfaced in the debug panel by the UI layer.
                case SimStateEventKind.SimStart:
                case SimStateEventKind.SimRunning:
                case SimStateEventKind.Pause:
                    break;
            }
        }
        Emit(fired);
        if (quit)
        {
            FireSimExit("sim Quit event");
        }
    }

    private void HandleCameraLocked(double value)
    {
        if (_pendingCamera is double p && (int)p == (int)value)
        {
            return; // already debouncing toward this value
        }
        if (_pendingCamera is null && _committedCamera is double c && (int)c == (int)value)
        {
            return; // no change
        }

        _pendingCamera = value;
        _debounceTimer?.Dispose();
        var debounce = TimeSpan.FromSeconds(_cfg.DebounceSeconds);
        if (debounce <= TimeSpan.Zero)
        {
            CommitCamera(value);
            return;
        }
        _debounceTimer = _time.CreateTimer(_ => CommitCamera(value), null, debounce, Timeout.InfiniteTimeSpan);
    }

    private void CommitCamera(double value)
    {
        List<Checkpoint>? fired = null;
        lock (_gate)
        {
            _pendingCamera = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            var previous = _committedCamera;
            _committedCamera = value;
            if (previous is double pv && (int)pv == (int)value)
            {
                return;
            }
            _log.LogInformation("CAMERA STATE committed: {Prev} -> {New}", previous, value);

            if (!_armed || _simExitFired)
            {
                return;
            }

            var cams = _cfg.CameraStates;

            if (_flightLoadedPending && !cams.IsMenuOrLoading(value))
            {
                fired = FireWorldLoadLocked(fired);
            }

            if (cams.IsCockpit(value) && !_cockpitFired && _simStartFired)
            {
                // Being in the cockpit implies the world is loaded — covers a missed
                // FlightLoaded event (e.g. we connected after the flight had loaded).
                if (!_worldLoadFired)
                {
                    _worldLoadFired = true;
                    _flightLoadedPending = false;
                    SetPhase(SessionPhase.WorldLoaded);
                    (fired ??= new()).Add(Checkpoint.OnWorldLoad);
                }
                _cockpitFired = true;
                SetPhase(SessionPhase.InCockpit);
                (fired ??= new()).Add(Checkpoint.OnEnterCockpit);
            }

            if (cams.IsMainMenu(value) && _connected && (_worldLoadFired || _cockpitFired))
            {
                // Back to the main menu with the connection alive: Exit Flight.
                // Re-arm the per-flight checkpoints for the next flight.
                _worldLoadFired = false;
                _cockpitFired = false;
                _flightLoadedPending = false;
                SetPhase(SessionPhase.InMenu);
                (fired ??= new()).Add(Checkpoint.OnExitFlight);
            }
        }
        Emit(fired);
    }

    private List<Checkpoint>? FireWorldLoadLocked(List<Checkpoint>? fired)
    {
        _flightLoadedPending = false;
        if (_worldLoadFired)
        {
            return fired;
        }
        _worldLoadFired = true;
        SetPhase(SessionPhase.WorldLoaded);
        (fired ??= new()).Add(Checkpoint.OnWorldLoad);
        return fired;
    }

    private void FireSimExit(string reason)
    {
        lock (_gate)
        {
            if (!_armed || _simExitFired)
            {
                return;
            }
            _simExitFired = true;
            _armed = false;
            _graceTimer?.Dispose();
            _graceTimer = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _flightLoadedPending = false;
            _log.LogInformation("Sim exit: {Reason}", reason);
            SetPhase(SessionPhase.Ended);
        }
        Emit(new List<Checkpoint> { Checkpoint.OnSimExit });
    }

    private void SetPhase(SessionPhase phase)
    {
        if (Phase == phase)
        {
            return;
        }
        Phase = phase;
        var handler = PhaseChanged;
        if (handler is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => handler(phase));
        }
    }

    private void Emit(List<Checkpoint>? fired)
    {
        if (fired is null)
        {
            return;
        }
        foreach (var cp in fired)
        {
            _log.LogInformation("Checkpoint reached: {Checkpoint}", cp);
            CheckpointReached?.Invoke(cp);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounceTimer?.Dispose();
            _graceTimer?.Dispose();
        }
    }
}
