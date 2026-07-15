using Microsoft.Extensions.Logging;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;
using SimLauncher.Core.Processes;

namespace SimLauncher.Core;

/// <summary>
/// Glue between the sim state source, the state machine, and the checkpoint engine.
///
/// SimConnect polling runs permanently (started by <see cref="Initialize"/>), so the
/// debug panel always reflects reality. When a connection is accepted and no session is
/// active, a session is armed automatically (unless disabled in config, or suppressed
/// because the user manually stopped the previous one — suppression clears when the sim
/// goes away). MSFS itself is launched only by <see cref="LaunchMsfsAndStartSessionAsync"/>:
/// it is not a managed app and is never shut down.
/// </summary>
public sealed class SessionCoordinator : IDisposable
{
    private readonly ISimStateSource _source;
    private readonly SessionStateMachine _stateMachine;
    private readonly CheckpointEngine _engine;
    private readonly ConfigStore _config;
    private readonly IProcessManager _procs;
    private readonly ILogger<SessionCoordinator> _log;

    public SessionStateMachine StateMachine => _stateMachine;
    public CheckpointEngine Engine => _engine;
    public ConfigStore Config => _config;

    /// <summary>Raised for every raw sim event, for the debug panel.</summary>
    public event EventHandler<SimStateEvent>? SimEvent;

    /// <summary>Raised with live aircraft data while connected, for the debug panel.</summary>
    public event EventHandler<SimTelemetry>? Telemetry;

    /// <summary>True after a manual stop while the sim keeps running; blocks auto-arm until the sim exits.</summary>
    public bool AutoArmSuppressed { get; private set; }

    public SessionCoordinator(
        ISimStateSource source,
        SessionStateMachine stateMachine,
        CheckpointEngine engine,
        ConfigStore config,
        IProcessManager procs,
        ILogger<SessionCoordinator> log)
    {
        _source = source;
        _stateMachine = stateMachine;
        _engine = engine;
        _config = config;
        _procs = procs;
        _log = log;

        _source.StateEvent += OnSimEvent;
        _source.TelemetryReceived += OnTelemetry;
        _stateMachine.CheckpointReached += OnCheckpoint;
        _config.ConfigChanged += OnConfigChanged;
    }

    /// <summary>Applies config and starts permanent SimConnect monitoring.</summary>
    public void Initialize()
    {
        ApplyConfig(_config.Current);
        _ = _source.StartAsync();
    }

    public bool IsSessionActive => _stateMachine.IsSessionActive;
    public bool IsSimConnected => _source.IsConnected;

    /// <summary>True when an MSFS process is detected (may predate the SimConnect connection).</summary>
    public bool IsMsfsProcessRunning()
        => _config.Current.Msfs.ProcessNames.Any(_procs.IsProcessRunning);

    /// <summary>Selects a profile by name (persisted). No-op while a session is active.</summary>
    public bool SelectProfile(string name)
    {
        if (_engine.IsSessionActive)
        {
            return false;
        }
        _config.Update(c => c.ActiveProfile = name);
        return true;
    }

    /// <summary>The master button: launch MSFS if it is not already up, then arm the session.</summary>
    public async Task LaunchMsfsAndStartSessionAsync()
    {
        if (_stateMachine.IsSessionActive)
        {
            return;
        }
        if (!IsMsfsProcessRunning() && !_source.IsConnected)
        {
            var msfs = _config.Current.Msfs;
            if (string.IsNullOrWhiteSpace(msfs.Path))
            {
                _log.LogError("Cannot launch MSFS: msfs.path is not configured");
            }
            else
            {
                _log.LogInformation("Launching MSFS via {Path}", msfs.Path);
                try
                {
                    // Never managed: the sim always outlives SimLauncher.
                    _procs.Start(new ProcessStartSpec(msfs.Path, msfs.Args, msfs.EffectiveShellExecute));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to launch MSFS from {Path}", msfs.Path);
                }
            }
        }
        await StartSessionAsync().ConfigureAwait(false);
    }

    /// <summary>Arms a session (checkpoint engine + state machine). Does not launch MSFS.</summary>
    public Task StartSessionAsync()
    {
        if (_stateMachine.IsSessionActive)
        {
            _log.LogWarning("Start Session ignored: already active");
            return Task.CompletedTask;
        }
        var profile = _config.Current.FindActiveProfile();
        if (profile is null)
        {
            _log.LogError("Cannot start session: no profile available");
            return Task.CompletedTask;
        }

        AutoArmSuppressed = false;
        _log.LogInformation("=== Session starting with profile '{Profile}' ===", profile.Name);
        _stateMachine.Configure(_config.Current.SimConnection);
        _engine.LoadProfile(profile);
        _engine.ArmSession();
        _stateMachine.StartSession();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Manual Stop/Teardown. Auto-arm is suppressed until the sim exits, so the session
    /// does not immediately re-arm against the still-running sim. Completes only after
    /// the engine has finished shutting managed apps down, so callers (e.g. app exit)
    /// can rely on teardown being done.
    /// </summary>
    public async Task StopSessionAsync()
    {
        _log.LogInformation("=== Session stop requested ===");
        AutoArmSuppressed = true;
        _stateMachine.StopSession(); // fires OnSimExit -> engine teardown
        await _engine.TeardownAsync().ConfigureAwait(false); // join that same teardown
    }

    private void OnSimEvent(object? sender, SimStateEvent e)
    {
        SimEvent?.Invoke(this, e);
        _stateMachine.Handle(e);

        switch (e.Kind)
        {
            case SimStateEventKind.ConnectionOpened
                when !_stateMachine.IsSessionActive
                     && !AutoArmSuppressed
                     && _config.Current.AutoStartSessionWhenSimDetected:
                _log.LogInformation("Running sim detected; arming session automatically");
                _ = StartSessionAsync();
                break;

            case SimStateEventKind.Quit:
            case SimStateEventKind.ConnectionLost:
                // The sim went away (or at least the connection did): a future sim is a
                // new session, so manual-stop suppression no longer applies.
                AutoArmSuppressed = false;
                break;
        }
    }

    private void OnTelemetry(object? sender, SimTelemetry telemetry)
        => Telemetry?.Invoke(this, telemetry);

    private void OnCheckpoint(Checkpoint checkpoint)
    {
        _ = FireEngineAsync(checkpoint);
    }

    private async Task FireEngineAsync(Checkpoint checkpoint)
    {
        try
        {
            await _engine.OnCheckpointAsync(checkpoint).ConfigureAwait(false);
            if (checkpoint == Checkpoint.OnSimExit)
            {
                _log.LogInformation("=== Session ended (monitoring continues) ===");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Checkpoint {Checkpoint} handling failed", checkpoint);
        }
    }

    private void OnConfigChanged(LauncherConfig config)
    {
        ApplyConfig(config);
    }

    private void ApplyConfig(LauncherConfig config)
    {
        _stateMachine.Configure(config.SimConnection);
        if (!_engine.IsSessionActive)
        {
            var profile = config.FindActiveProfile();
            if (profile is not null)
            {
                _engine.LoadProfile(profile);
            }
        }
    }

    public void Dispose()
    {
        _source.StateEvent -= OnSimEvent;
        _source.TelemetryReceived -= OnTelemetry;
        _stateMachine.CheckpointReached -= OnCheckpoint;
        _config.ConfigChanged -= OnConfigChanged;
    }
}
