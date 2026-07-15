using System.Collections.ObjectModel;
using System.Windows.Threading;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;

namespace SimLauncher.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly Checkpoint[] SectionOrder =
    {
        Checkpoint.LauncherStart,
        Checkpoint.OnSimStart,
        Checkpoint.OnWorldLoad,
        Checkpoint.OnEnterCockpit,
        Checkpoint.OnExitFlight,
        Checkpoint.OnSimExit,
    };

    private readonly SessionCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tick;
    private readonly HashSet<Checkpoint> _completed = new();

    private bool _isSessionActive;
    private bool _msfsRunning;
    private bool _connected;
    private bool _sessionButtonEnabled = true;
    private string _sessionButtonText = "Launch MSFS";
    private string _sessionStatusText = "";
    private string _connectionText = "Not connected";
    private string _cameraText = "—";
    private string _phaseText = "Idle";
    private string _aircraftText = "—";
    private string _positionText = "—";
    private string _configErrorText = "";
    private string? _selectedProfile;
    private bool _debugOpen;

    public MainViewModel(SessionCoordinator coordinator)
    {
        _coordinator = coordinator;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var cp in SectionOrder)
        {
            Sections.Add(new SectionViewModel(cp, OnAddEvent));
        }

        StartStopCommand = new RelayCommand(_ => _ = ToggleSessionAsync());

        _coordinator.Engine.AppChanged += app => Post(() => RefreshRow(app));
        _coordinator.StateMachine.CheckpointReached += cp => Post(() => OnCheckpointReached(cp));
        _coordinator.StateMachine.PhaseChanged += phase => Post(() => OnPhaseChanged(phase));
        _coordinator.SimEvent += (_, e) => Post(() => OnSimEvent(e));
        _coordinator.Telemetry += (_, t) => Post(() => OnTelemetry(t));
        _coordinator.Config.ConfigChanged += _ => Post(RebuildFromConfig);
        _coordinator.Config.ConfigError += errors => Post(() =>
            ConfigErrorText = "Config error: " + string.Join(" · ", errors));
        UiLogSink.Instance.LineEmitted += line => Post(() => AppendLog(line));

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => OnTick(), _dispatcher);
        _tick.Start();

        RebuildFromConfig();
        UpdateSessionState();
    }

    public ObservableCollection<SectionViewModel> Sections { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public ObservableCollection<string> RecentSimEvents { get; } = new();
    public ObservableCollection<string> SessionLog { get; } = new();

    public RelayCommand StartStopCommand { get; }

    /// <summary>Set by the window: opens the editor for a new/existing event. Returns true if saved.</summary>
    public Func<AppConfig, bool, bool>? EditDialog { get; set; }

    public bool IsSessionActive { get => _isSessionActive; private set => Set(ref _isSessionActive, value); }
    public bool SessionButtonEnabled { get => _sessionButtonEnabled; private set => Set(ref _sessionButtonEnabled, value); }
    public string SessionButtonText { get => _sessionButtonText; private set => Set(ref _sessionButtonText, value); }
    public string SessionStatusText { get => _sessionStatusText; private set => Set(ref _sessionStatusText, value); }
    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public string CameraText { get => _cameraText; private set => Set(ref _cameraText, value); }
    public string PhaseText { get => _phaseText; private set => Set(ref _phaseText, value); }
    public string AircraftText { get => _aircraftText; private set => Set(ref _aircraftText, value); }
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }
    public string ConfigErrorText { get => _configErrorText; private set => Set(ref _configErrorText, value); }
    public bool HasConfigError => !string.IsNullOrEmpty(_configErrorText);
    public bool DebugOpen { get => _debugOpen; set => Set(ref _debugOpen, value); }

    /// <summary>Registry-backed; no cached field so the tray toggle and this stay consistent.</summary>
    public bool LaunchOnStartup
    {
        get => StartupManager.IsEnabled();
        set
        {
            StartupManager.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (Set(ref _selectedProfile, value) && value is not null
                && value != _coordinator.Config.Current.ActiveProfile)
            {
                _coordinator.SelectProfile(value);
            }
        }
    }

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }

    /// <summary>Master button / tray action.</summary>
    public async Task ToggleSessionAsync()
    {
        if (IsSessionActive)
        {
            await _coordinator.StopSessionAsync();
        }
        else
        {
            ConfigErrorText = "";
            OnPropertyChanged(nameof(HasConfigError));
            _completed.Clear();
            if (_msfsRunning || _connected)
            {
                await _coordinator.StartSessionAsync(); // sim already up: just arm
            }
            else
            {
                await _coordinator.LaunchMsfsAndStartSessionAsync();
            }
        }
        UpdateSessionState();
    }

    private void OnTick()
    {
        _msfsRunning = _coordinator.IsMsfsProcessRunning();
        _connected = _coordinator.IsSimConnected;
        UpdateSessionState();
        RefreshAllRows();
    }

    private void UpdateSessionState()
    {
        IsSessionActive = _coordinator.IsSessionActive;
        var simUp = _msfsRunning || _connected;
        var autoArmComing = !_coordinator.AutoArmSuppressed
                            && _coordinator.Config.Current.AutoStartSessionWhenSimDetected;

        if (IsSessionActive)
        {
            SessionButtonText = "Stop Session";
            SessionButtonEnabled = true;
            SessionStatusText = _coordinator.StateMachine.Phase switch
            {
                SessionPhase.WaitingForSim => "Waiting for SimConnect…",
                SessionPhase.SimRunning => "Sim running",
                SessionPhase.WorldLoaded => "World loaded",
                SessionPhase.InCockpit => "In cockpit",
                SessionPhase.InMenu => "In main menu",
                _ => "",
            };
        }
        else if (simUp && autoArmComing)
        {
            // The session arms itself as soon as SimConnect accepts; nothing to click.
            SessionButtonText = "MSFS Running";
            SessionButtonEnabled = false;
            SessionStatusText = _connected ? "Connected — arming session…" : "Waiting for SimConnect…";
        }
        else if (simUp)
        {
            SessionButtonText = "Start Session";
            SessionButtonEnabled = true;
            SessionStatusText = "MSFS running — session stopped";
        }
        else
        {
            SessionButtonText = "Launch MSFS";
            SessionButtonEnabled = true;
            SessionStatusText = "";
        }
        UpdateSectionStates();
    }

    private void OnCheckpointReached(Checkpoint cp)
    {
        _completed.Add(cp);
        UpdateSectionStates();
    }

    private void OnPhaseChanged(SessionPhase phase)
    {
        PhaseText = phase.ToString();
        UpdateSessionState();
        RefreshAllRows();
    }

    private void OnSimEvent(SimStateEvent e)
    {
        if (e.Kind == SimStateEventKind.CameraState)
        {
            CameraText = $"{e.Value:0}";
        }
        else
        {
            RecentSimEvents.Insert(0, $"{DateTime.Now:HH:mm:ss}  {e}");
            while (RecentSimEvents.Count > 5)
            {
                RecentSimEvents.RemoveAt(RecentSimEvents.Count - 1);
            }
        }
        switch (e.Kind)
        {
            case SimStateEventKind.ConnectionOpened:
                _connected = true;
                ConnectionText = "Connected";
                break;
            case SimStateEventKind.ConnectionLost:
                _connected = false;
                ConnectionText = "Connection lost";
                ResetTelemetry();
                break;
            case SimStateEventKind.Quit:
                _connected = false;
                ConnectionText = "Sim quit";
                ResetTelemetry();
                break;
        }
        UpdateSessionState();
    }

    private void OnTelemetry(SimTelemetry t)
    {
        AircraftText = string.IsNullOrWhiteSpace(t.AircraftTitle) ? "—" : t.AircraftTitle;
        PositionText = $"{t.Latitude:0.0000}, {t.Longitude:0.0000} · {t.AltitudeFeet:0} ft · {t.IndicatedAirspeedKnots:0} kt";
    }

    private void ResetTelemetry()
    {
        AircraftText = "—";
        PositionText = "—";
        CameraText = "—";
    }

    private void UpdateSectionStates()
    {
        var activeCp = _coordinator.StateMachine.Phase switch
        {
            SessionPhase.WaitingForSim => (Checkpoint?)Checkpoint.LauncherStart,
            SessionPhase.SimRunning => Checkpoint.OnSimStart,
            SessionPhase.WorldLoaded => Checkpoint.OnWorldLoad,
            SessionPhase.InCockpit => Checkpoint.OnEnterCockpit,
            SessionPhase.InMenu => Checkpoint.OnExitFlight,
            SessionPhase.Ended => Checkpoint.OnSimExit,
            _ => null,
        };
        foreach (var section in Sections)
        {
            section.IsActive = IsSessionActive && section.Checkpoint == activeCp;
            section.IsCompleted = _completed.Contains(section.Checkpoint);
        }
    }

    private void AppendLog(string line)
    {
        SessionLog.Add(line);
        while (SessionLog.Count > 500)
        {
            SessionLog.RemoveAt(0);
        }
    }

    // ----- rows / sections -----

    private void RebuildFromConfig()
    {
        var config = _coordinator.Config.Current;

        var profiles = config.Profiles.Select(p => p.Name).ToList();
        Profiles.Clear();
        foreach (var name in profiles)
        {
            Profiles.Add(name);
        }
        _selectedProfile = config.FindActiveProfile()?.Name;
        OnPropertyChanged(nameof(SelectedProfile));

        RebuildRows();
        if (!IsSessionActive)
        {
            ConfigErrorText = "";
        }
        OnPropertyChanged(nameof(HasConfigError));
    }

    private void RebuildRows()
    {
        var apps = _coordinator.Engine.Apps;
        foreach (var section in Sections)
        {
            section.Rows.Clear();
            foreach (var app in apps.Where(a => a.Config.Checkpoint == section.Checkpoint))
            {
                section.Rows.Add(new AppRowViewModel(app, OnEditRow));
            }
        }
        RefreshAllRows();
    }

    private void RefreshRow(ManagedApp app)
    {
        var row = Sections.SelectMany(s => s.Rows).FirstOrDefault(r => ReferenceEquals(r.App, app));
        if (row is null)
        {
            RebuildRows(); // engine reloaded the profile; row objects are stale
            return;
        }
        row.Refresh(IsSessionActive);
    }

    private void RefreshAllRows()
    {
        foreach (var row in Sections.SelectMany(s => s.Rows))
        {
            row.Refresh(IsSessionActive);
        }
        OnPropertyChanged(nameof(HasConfigError));
    }

    // ----- editing -----

    private void OnAddEvent(Checkpoint checkpoint)
    {
        var draft = new AppConfig { Checkpoint = checkpoint };
        if (EditDialog?.Invoke(draft, false) == true)
        {
            _coordinator.Config.Update(c =>
            {
                c.FindActiveProfile()?.Apps.Add(draft);
            });
        }
    }

    private void OnEditRow(AppRowViewModel row)
    {
        var original = row.App.Config;
        // Edit a copy so cancel leaves the config untouched.
        var draft = new AppConfig
        {
            Name = original.Name,
            Path = original.Path,
            Args = original.Args,
            Checkpoint = original.Checkpoint,
            DelaySeconds = original.DelaySeconds,
            WaitForApp = original.WaitForApp,
            WaitForAppReadySeconds = original.WaitForAppReadySeconds,
            Shutdown = original.Shutdown,
            ShutdownTimeoutSeconds = original.ShutdownTimeoutSeconds,
            RestartIfCrashed = original.RestartIfCrashed,
            AlreadyRunning = original.AlreadyRunning,
            ShellExecute = original.ShellExecute,
            RunAsAdmin = original.RunAsAdmin,
        };
        if (EditDialog?.Invoke(draft, true) == true)
        {
            _coordinator.Config.Update(c =>
            {
                var profile = c.FindActiveProfile();
                if (profile is null)
                {
                    return;
                }
                var index = profile.Apps.IndexOf(original);
                if (draft.Name == DeleteSentinel.Name)
                {
                    if (index >= 0)
                    {
                        profile.Apps.RemoveAt(index);
                    }
                    return;
                }
                if (index >= 0)
                {
                    profile.Apps[index] = draft;
                }
            });
        }
    }

    /// <summary>Marker the edit dialog uses to signal deletion.</summary>
    public static readonly AppConfig DeleteSentinel = new() { Name = "\0__delete__" };

    /// <summary>App names in the active profile, for the waitForApp picker.</summary>
    public IReadOnlyList<string> GetAppNames(Checkpoint checkpoint, string? exclude)
        => _coordinator.Config.Current.FindActiveProfile()?.Apps
               .Where(a => a.Checkpoint == checkpoint
                           && !string.Equals(a.Name, exclude, StringComparison.OrdinalIgnoreCase))
               .Select(a => a.Name)
               .ToList()
           ?? new List<string>();
}
