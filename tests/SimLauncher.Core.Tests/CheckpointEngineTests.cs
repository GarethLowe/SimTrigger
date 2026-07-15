using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;
using Xunit;

namespace SimLauncher.Core.Tests;

public sealed class CheckpointEngineTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeProcessManager _procs = new();
    private readonly CheckpointEngine _engine;

    public CheckpointEngineTests()
    {
        _engine = new CheckpointEngine(_procs, NullLogger<CheckpointEngine>.Instance, _time);
    }

    public void Dispose() => _engine.Dispose();

    private static AppConfig App(string name, Checkpoint cp, Action<AppConfig>? mutate = null)
    {
        var app = new AppConfig { Name = name, Path = $@"C:\apps\{name}.exe", Checkpoint = cp };
        mutate?.Invoke(app);
        return app;
    }

    private ManagedApp State(string name) => _engine.Apps.Single(a => a.Name == name);

    private void Arm(params AppConfig[] apps)
    {
        _engine.LoadProfile(new ProfileConfig { Name = "test", Apps = apps.ToList() });
        _engine.ArmSession();
    }

    /// <summary>Advances fake time in small steps, yielding real time so async continuations run.</summary>
    private async Task AdvanceUntilAsync(Func<bool> condition, double maxFakeSeconds = 120)
    {
        var advanced = 0.0;
        while (!condition() && advanced < maxFakeSeconds)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            advanced += 0.25;
            await Task.Delay(2);
        }
        await SettleAsync();
        Assert.True(condition(), "condition not reached within simulated time budget");
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task ImmediateApp_LaunchesWhenCheckpointFires()
    {
        Arm(App("BeyondATC", Checkpoint.OnSimStart));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.Single(_procs.Starts);
        Assert.Equal(AppStatus.Running, State("BeyondATC").Status);
        Assert.Equal(_procs.LastProcess!.Pid, State("BeyondATC").Pid);
    }

    [Fact]
    public async Task App_AtOtherCheckpoint_DoesNotLaunch()
    {
        Arm(App("AutoFPS", Checkpoint.OnWorldLoad));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();
        Assert.Empty(_procs.Starts);
    }

    [Fact]
    public async Task DelayedApp_CountsDown_ThenLaunches()
    {
        Arm(App("SPAD", Checkpoint.OnSimStart, a => a.DelaySeconds = 5));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.Equal(AppStatus.Delayed, State("SPAD").Status);
        Assert.NotNull(State("SPAD").CountdownEndsAt);
        Assert.Empty(_procs.Starts);

        await AdvanceUntilAsync(() => State("SPAD").Status == AppStatus.Running);
        Assert.Single(_procs.Starts);
    }

    [Fact]
    public async Task DelayedApp_IsCancelledByTeardown()
    {
        Arm(App("SPAD", Checkpoint.OnSimStart, a => a.DelaySeconds = 60));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();
        Assert.Equal(AppStatus.Delayed, State("SPAD").Status);

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();
        _time.Advance(TimeSpan.FromSeconds(120));
        await SettleAsync();

        Assert.Empty(_procs.Starts); // the pending launch was aborted
        Assert.False(_engine.IsSessionActive);
    }

    [Fact]
    public async Task DelayedApp_AtWorldLoad_IsCancelledByExitFlight()
    {
        Arm(App("AutoFPS", Checkpoint.OnWorldLoad, a => a.DelaySeconds = 30));
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await SettleAsync();
        Assert.Equal(AppStatus.Delayed, State("AutoFPS").Status);

        await _engine.OnCheckpointAsync(Checkpoint.OnExitFlight);
        await SettleAsync();
        _time.Advance(TimeSpan.FromSeconds(60));
        await SettleAsync();

        Assert.Empty(_procs.Starts);
        Assert.Equal(AppStatus.Waiting, State("AutoFPS").Status); // re-armed for the next flight
        Assert.True(_engine.IsSessionActive); // Exit Flight is not teardown

        // Next flight: world load fires again and the app launches this time.
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await AdvanceUntilAsync(() => State("AutoFPS").Status == AppStatus.Running);
        Assert.Single(_procs.Starts);
    }

    [Fact]
    public async Task WaitForApp_OrdersLaunchesWithinCheckpoint()
    {
        Arm(
            App("REX", Checkpoint.OnSimStart),
            App("BeyondATC", Checkpoint.OnSimStart, a =>
            {
                a.WaitForApp = "REX";
                a.WaitForAppReadySeconds = 10;
            }));

        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.Equal(AppStatus.Running, State("REX").Status);
        Assert.Equal(AppStatus.WaitingForDependency, State("BeyondATC").Status);
        Assert.Single(_procs.Starts);

        await AdvanceUntilAsync(() => State("BeyondATC").Status == AppStatus.Running);
        Assert.Equal(2, _procs.Starts.Count);
        Assert.Equal("REX", Path.GetFileNameWithoutExtension(_procs.Starts[0].Spec.Path));
        Assert.Equal("BeyondATC", Path.GetFileNameWithoutExtension(_procs.Starts[1].Spec.Path));
        // Ready gap honoured on the fake clock.
        Assert.True(State("BeyondATC").StartedAt - State("REX").StartedAt >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RefiredCheckpoint_DoesNotRelaunchRunningApp()
    {
        Arm(App("AutoFPS", Checkpoint.OnWorldLoad));
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await SettleAsync();
        Assert.Single(_procs.Starts);

        // fly -> menu -> fly again
        await _engine.OnCheckpointAsync(Checkpoint.OnExitFlight);
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await SettleAsync();

        Assert.Single(_procs.Starts); // still just the one instance
    }

    [Fact]
    public async Task RefiredCheckpoint_RelaunchesAppThatExited()
    {
        Arm(App("AutoFPS", Checkpoint.OnWorldLoad));
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await SettleAsync();
        _procs.LastProcess!.MarkExited();
        await SettleAsync();
        Assert.Equal(AppStatus.Exited, State("AutoFPS").Status);

        await _engine.OnCheckpointAsync(Checkpoint.OnExitFlight);
        await _engine.OnCheckpointAsync(Checkpoint.OnWorldLoad);
        await SettleAsync();

        Assert.Equal(2, _procs.Starts.Count);
    }

    [Fact]
    public async Task Teardown_Graceful_ClosesWindow_NoKillWhenItComplies()
    {
        Arm(App("BeyondATC", Checkpoint.OnSimStart));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();
        var process = _procs.LastProcess!;

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();

        Assert.True(process.CloseRequested);
        Assert.False(process.Killed);
        Assert.Equal(AppStatus.Exited, State("BeyondATC").Status);
    }

    [Fact]
    public async Task Teardown_Graceful_KillsAfterTimeout()
    {
        Arm(App("Stubborn", Checkpoint.OnSimStart, a => a.ShutdownTimeoutSeconds = 10));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();
        var process = _procs.LastProcess!;
        process.RespondsToClose = false;

        var teardown = _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await AdvanceUntilAsync(() => process.Killed, maxFakeSeconds: 30);
        await teardown;

        Assert.True(process.CloseRequested);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Teardown_KillMode_KillsImmediately()
    {
        Arm(App("AutoFPS", Checkpoint.OnSimStart, a => a.Shutdown = ShutdownMode.Kill));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();

        Assert.True(_procs.LastProcess!.Killed);
        Assert.False(_procs.LastProcess.CloseRequested);
    }

    [Fact]
    public async Task Teardown_LeaveMode_NeverTouchesProcess()
    {
        Arm(App("MSFS", Checkpoint.LauncherStart, a => a.Shutdown = ShutdownMode.Leave));
        await _engine.OnCheckpointAsync(Checkpoint.LauncherStart);
        await SettleAsync();
        var process = _procs.LastProcess!;

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();

        Assert.False(process.CloseRequested);
        Assert.False(process.Killed);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task AlreadyRunning_Skip_DoesNotStartOrManage()
    {
        var cfg = App("BeyondATC", Checkpoint.OnSimStart);
        _procs.Existing[cfg.Path] = new FakeProcess(42);
        Arm(cfg);

        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.Empty(_procs.Starts);
        Assert.Equal(AppStatus.Skipped, State("BeyondATC").Status);

        // Teardown must not touch the skipped instance.
        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();
        Assert.False(_procs.Existing[cfg.Path].Killed);
        Assert.False(_procs.Existing[cfg.Path].CloseRequested);
    }

    [Fact]
    public async Task AlreadyRunning_Adopt_ManagesButLeavesOnTeardownByDefault()
    {
        var cfg = App("BeyondATC", Checkpoint.OnSimStart, a => a.AlreadyRunning = AlreadyRunningBehavior.Adopt);
        var existing = new FakeProcess(42);
        _procs.Existing[cfg.Path] = existing;
        Arm(cfg);

        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        var state = State("BeyondATC");
        Assert.Equal(AppStatus.Running, state.Status);
        Assert.True(state.IsAdopted);
        Assert.Equal(42, state.Pid);
        Assert.Empty(_procs.Starts); // adopted, not launched

        // Default shutdown for adopted processes is Leave.
        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();
        Assert.False(existing.Killed);
        Assert.False(existing.CloseRequested);
    }

    [Fact]
    public async Task AlreadyRunning_Adopt_WithExplicitShutdown_IsShutDown()
    {
        var cfg = App("BeyondATC", Checkpoint.OnSimStart, a =>
        {
            a.AlreadyRunning = AlreadyRunningBehavior.Adopt;
            a.Shutdown = ShutdownMode.Kill;
        });
        var existing = new FakeProcess(42);
        _procs.Existing[cfg.Path] = existing;
        Arm(cfg);

        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();

        Assert.True(existing.Killed);
    }

    [Fact]
    public async Task AlreadyRunning_StartAnother_LaunchesSecondInstance()
    {
        var cfg = App("Notepad", Checkpoint.OnSimStart, a => a.AlreadyRunning = AlreadyRunningBehavior.StartAnother);
        _procs.Existing[cfg.Path] = new FakeProcess(42);
        Arm(cfg);

        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.Single(_procs.Starts);
        Assert.Equal(AppStatus.Running, State("Notepad").Status);
        Assert.NotEqual(42, State("Notepad").Pid);
    }

    [Fact]
    public async Task RestartIfCrashed_RelaunchesWithBackoff_MaxThreeAttempts()
    {
        Arm(App("REX", Checkpoint.OnSimStart, a => a.RestartIfCrashed = true));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        for (var crash = 1; crash <= 3; crash++)
        {
            _procs.LastProcess!.MarkExited();
            await AdvanceUntilAsync(() => _procs.Starts.Count == crash + 1, maxFakeSeconds: 30);
        }
        Assert.Equal(4, _procs.Starts.Count); // original + 3 restarts

        // Fourth crash: budget exhausted, no more restarts.
        _procs.LastProcess!.MarkExited();
        _time.Advance(TimeSpan.FromSeconds(60));
        await SettleAsync();
        Assert.Equal(4, _procs.Starts.Count);
        Assert.Equal(AppStatus.Failed, State("REX").Status);
    }

    [Fact]
    public async Task CrashDuringTeardown_DoesNotRestart()
    {
        Arm(App("REX", Checkpoint.OnSimStart, a => a.RestartIfCrashed = true));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit); // graceful close exits it
        await SettleAsync();
        _time.Advance(TimeSpan.FromSeconds(60));
        await SettleAsync();

        Assert.Single(_procs.Starts);
    }

    [Fact]
    public async Task UriLaunch_WithNoProcessHandle_ReportsRunningUnmanaged()
    {
        var cfg = App("MSFS", Checkpoint.LauncherStart, a =>
        {
            a.Path = "steam://rungameid/2537590";
            a.Shutdown = ShutdownMode.Leave;
        });
        _procs.ReturnNullFor = spec => spec.Path.StartsWith("steam://");
        Arm(cfg);

        await _engine.OnCheckpointAsync(Checkpoint.LauncherStart);
        await SettleAsync();

        var state = State("MSFS");
        Assert.Equal(AppStatus.Running, state.Status);
        Assert.Null(state.Pid);
    }

    [Fact]
    public async Task RunAsAdmin_FlowsIntoStartSpec()
    {
        Arm(App("REX", Checkpoint.OnSimStart, a => a.RunAsAdmin = true));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        Assert.True(_procs.Starts.Single().Spec.RunAsAdmin);
    }

    [Fact]
    public async Task OnSimExitApps_LaunchAfterTeardown()
    {
        Arm(
            App("BeyondATC", Checkpoint.OnSimStart),
            App("LogbookExport", Checkpoint.OnSimExit));
        await _engine.OnCheckpointAsync(Checkpoint.OnSimStart);
        await SettleAsync();

        await _engine.OnCheckpointAsync(Checkpoint.OnSimExit);
        await SettleAsync();

        Assert.True(_procs.Starts.Single(s => s.Spec.Path.Contains("BeyondATC")).Process.HasExited);
        var exporter = _procs.Starts.Single(s => s.Spec.Path.Contains("LogbookExport"));
        Assert.False(exporter.Process.HasExited);
    }
}
