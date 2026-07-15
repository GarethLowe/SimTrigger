using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;
using Xunit;

namespace SimLauncher.Core.Tests;

/// <summary>Full-flow tests: fake sim source in, process launches out (acceptance criterion 1 against fakes).</summary>
public sealed class SessionCoordinatorTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSimStateSource _source = new();
    private readonly FakeProcessManager _procs = new();
    private readonly SessionStateMachine _sm;
    private readonly CheckpointEngine _engine;
    private readonly ConfigStore _config;
    private readonly SessionCoordinator _coordinator;
    private readonly string _configPath;

    public SessionCoordinatorTests()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"simlauncher-test-{Guid.NewGuid():N}.json");
        _sm = new SessionStateMachine(NullLogger<SessionStateMachine>.Instance, _time);
        _engine = new CheckpointEngine(_procs, NullLogger<CheckpointEngine>.Instance, _time);
        _config = new ConfigStore(NullLogger<ConfigStore>.Instance, _configPath);
        _config.Load();
        _config.Update(c =>
        {
            c.ActiveProfile = "e2e";
            c.Msfs = new MsfsConfig
            {
                Path = @"C:\sim\msfs.exe",
                ShellExecute = false,
                ProcessNames = { "msfs" },
            };
            c.Profiles.Clear();
            c.Profiles.Add(new ProfileConfig
            {
                Name = "e2e",
                Apps =
                {
                    new AppConfig { Name = "SPAD", Path = @"C:\apps\spad.exe", Checkpoint = Checkpoint.LauncherStart },
                    new AppConfig { Name = "REX", Path = @"C:\apps\rex.exe", Checkpoint = Checkpoint.OnSimStart },
                    new AppConfig { Name = "AutoFPS", Path = @"C:\apps\autofps.exe", Checkpoint = Checkpoint.OnWorldLoad, Shutdown = ShutdownMode.Kill },
                },
            });
        });
        _coordinator = new SessionCoordinator(_source, _sm, _engine, _config, _procs,
            NullLogger<SessionCoordinator>.Instance);
        _coordinator.Initialize();
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _engine.Dispose();
        _sm.Dispose();
        _config.Dispose();
        File.Delete(_configPath);
    }

    private ManagedApp State(string name) => _engine.Apps.Single(a => a.Name == name);

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(5);
        }
    }

    private void CommitCamera(double value)
    {
        _source.Raise(SimStateEventKind.CameraState, value);
        _time.Advance(TimeSpan.FromSeconds(2.1));
    }

    [Fact]
    public void Initialize_StartsMonitoringImmediately()
    {
        Assert.True(_source.Started);
        Assert.False(_coordinator.IsSessionActive);
    }

    [Fact]
    public async Task LaunchMsfs_StartsSimViaConfiguredPath()
    {
        await _coordinator.LaunchMsfsAndStartSessionAsync();
        await SettleAsync();

        Assert.Single(_procs.Starts, s => s.Spec.Path == @"C:\sim\msfs.exe");
        Assert.True(_coordinator.IsSessionActive);
        Assert.Equal(AppStatus.Running, State("SPAD").Status); // alongside app armed + launched
    }

    [Fact]
    public async Task LaunchMsfs_SkipsLaunch_WhenSimProcessAlreadyRunning()
    {
        _procs.RunningProcessNames.Add("msfs");
        await _coordinator.LaunchMsfsAndStartSessionAsync();
        await SettleAsync();

        Assert.DoesNotContain(_procs.Starts, s => s.Spec.Path == @"C:\sim\msfs.exe");
        Assert.True(_coordinator.IsSessionActive); // still arms the session
    }

    [Fact]
    public async Task FullSession_LaunchesAtEachCheckpoint_ThenTearsDown()
    {
        await _coordinator.LaunchMsfsAndStartSessionAsync();
        await SettleAsync();

        Assert.Equal(AppStatus.Running, State("SPAD").Status);
        Assert.Equal(AppStatus.Waiting, State("REX").Status);

        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();
        Assert.Equal(AppStatus.Running, State("REX").Status);
        Assert.Equal(AppStatus.Waiting, State("AutoFPS").Status);

        _source.Raise(SimStateEventKind.FlightLoaded);
        CommitCamera(2);
        await SettleAsync();
        Assert.Equal(AppStatus.Running, State("AutoFPS").Status);

        var rex = _procs.Starts.Single(s => s.Spec.Path.Contains("rex")).Process;
        var autofps = _procs.Starts.Single(s => s.Spec.Path.Contains("autofps")).Process;

        _source.Raise(SimStateEventKind.Quit);
        await SettleAsync();

        Assert.True(rex.CloseRequested); // graceful
        Assert.True(rex.HasExited);
        Assert.True(autofps.Killed); // kill mode
        Assert.False(_engine.IsSessionActive);
        Assert.True(_source.Started); // monitoring continues after the session ends
    }

    [Fact]
    public async Task AutoArm_WhenRunningSimIsDetected()
    {
        // No button press: the sim shows up on its own.
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();

        Assert.True(_coordinator.IsSessionActive);
        Assert.Equal(AppStatus.Running, State("SPAD").Status);
        Assert.Equal(AppStatus.Running, State("REX").Status);
        Assert.DoesNotContain(_procs.Starts, s => s.Spec.Path == @"C:\sim\msfs.exe"); // sim NOT launched
    }

    [Fact]
    public async Task AutoArm_CanBeDisabledInConfig()
    {
        _config.Update(c => c.AutoStartSessionWhenSimDetected = false);
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();

        Assert.False(_coordinator.IsSessionActive);
    }

    [Fact]
    public async Task AutoArm_MidFlightAttach_CatchesUpToWorldLoadAndCockpit()
    {
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();
        CommitCamera(2); // user is already sitting in the cockpit
        await SettleAsync();

        Assert.Equal(AppStatus.Running, State("AutoFPS").Status); // OnWorldLoad fired without FlightLoaded
        Assert.Equal(SessionPhase.InCockpit, _sm.Phase);
    }

    [Fact]
    public async Task ManualStop_SuppressesAutoArm_UntilSimGoesAway()
    {
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();
        Assert.True(_coordinator.IsSessionActive);

        await _coordinator.StopSessionAsync();
        await SettleAsync();
        Assert.False(_coordinator.IsSessionActive);
        Assert.True(_coordinator.AutoArmSuppressed);

        // Sim still running; a SimConnect blip must not re-arm against the user's wishes...
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();
        Assert.False(_coordinator.IsSessionActive);

        // ...but once the sim exits and a NEW sim appears, that is a new session.
        _source.Raise(SimStateEventKind.ConnectionLost);
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();
        Assert.True(_coordinator.IsSessionActive);
    }

    [Fact]
    public async Task Telemetry_IsForwardedForDebugPanel()
    {
        SimTelemetry? received = null;
        _coordinator.Telemetry += (_, t) => received = t;
        _source.RaiseTelemetry(new SimTelemetry("Cessna 172", 47.1, -122.3, 1500, 105));
        await SettleAsync();

        Assert.Equal("Cessna 172", received?.AircraftTitle);
        Assert.Equal(1500, received?.AltitudeFeet);
    }

    [Fact]
    public async Task TransientDisconnect_DoesNotTearDown_Apps()
    {
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();

        _source.Raise(SimStateEventKind.ConnectionLost);
        _time.Advance(TimeSpan.FromSeconds(10)); // < 30 s grace
        _source.Raise(SimStateEventKind.ConnectionOpened);
        _time.Advance(TimeSpan.FromSeconds(120));
        await SettleAsync();

        Assert.Equal(AppStatus.Running, State("REX").Status);
        Assert.True(_engine.IsSessionActive);
    }

    [Fact]
    public async Task DisconnectBeyondGrace_TearsDown()
    {
        _source.Raise(SimStateEventKind.ConnectionOpened);
        await SettleAsync();

        _source.Raise(SimStateEventKind.ConnectionLost);
        _time.Advance(TimeSpan.FromSeconds(31));
        await SettleAsync();

        Assert.False(_engine.IsSessionActive);
        Assert.True(_procs.Starts.Single(s => s.Spec.Path.Contains("rex")).Process.HasExited);
    }

    [Fact]
    public void MsfsMigration_LiftsMsfsRowsOutOfProfiles()
    {
        // Simulate a pre-migration config on disk, then reload through the store.
        var store2Path = Path.Combine(Path.GetTempPath(), $"simlauncher-mig-{Guid.NewGuid():N}.json");
        File.WriteAllText(store2Path, """
        {
          "activeProfile": "p",
          "profiles": [{
            "name": "p",
            "apps": [
              { "name": "MSFS 2024", "path": "steam://rungameid/2537590", "checkpoint": "launcherStart", "shutdown": "leave" },
              { "name": "SPAD", "path": "C:\\apps\\spad.exe", "checkpoint": "launcherStart" }
            ]
          }]
        }
        """);
        using var store = new ConfigStore(NullLogger<ConfigStore>.Instance, store2Path);
        var errors = store.Load();

        Assert.Empty(errors);
        Assert.Equal("steam://rungameid/2537590", store.Current.Msfs.Path);
        Assert.True(store.Current.Msfs.EffectiveShellExecute);
        var profile = store.Current.Profiles.Single();
        Assert.Single(profile.Apps);
        Assert.Equal("SPAD", profile.Apps[0].Name);
        File.Delete(store2Path);
    }
}
