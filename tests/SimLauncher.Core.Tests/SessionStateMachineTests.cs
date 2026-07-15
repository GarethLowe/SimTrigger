using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using Xunit;

namespace SimLauncher.Core.Tests;

public sealed class SessionStateMachineTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly SessionStateMachine _sm;
    private readonly List<Checkpoint> _fired = new();
    private readonly SimConnectionConfig _cfg = new(); // debounce 2s, grace 30s

    public SessionStateMachineTests()
    {
        _sm = new SessionStateMachine(NullLogger<SessionStateMachine>.Instance, _time);
        _sm.Configure(_cfg);
        _sm.CheckpointReached += _fired.Add;
    }

    public void Dispose() => _sm.Dispose();

    private void Camera(double value, bool commit = true)
    {
        _sm.Handle(new SimStateEvent(SimStateEventKind.CameraState, value));
        if (commit)
        {
            _time.Advance(TimeSpan.FromSeconds(_cfg.DebounceSeconds + 0.1));
        }
    }

    [Fact]
    public void StartSession_FiresLauncherStart()
    {
        _sm.StartSession();
        Assert.Equal(new[] { Checkpoint.LauncherStart }, _fired);
        Assert.Equal(SessionPhase.WaitingForSim, _sm.Phase);
    }

    [Fact]
    public void ConnectionOpened_FiresOnSimStart_OncePerSession()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionLost));
        _time.Advance(TimeSpan.FromSeconds(5)); // within grace
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));

        Assert.Equal(1, _fired.Count(c => c == Checkpoint.OnSimStart));
    }

    [Fact]
    public void ConnectionOpened_WithoutStartSession_FiresNothing()
    {
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        Camera(2);
        Assert.Empty(_fired);
    }

    [Fact]
    public void FlightLoaded_WhileInMenu_IsGatedUntilCameraLeavesMenu()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        Camera(11); // main menu

        // The menu-transition quirk: FlightLoaded fires while still in the menu.
        _sm.Handle(new SimStateEvent(SimStateEventKind.FlightLoaded));
        Assert.DoesNotContain(Checkpoint.OnWorldLoad, _fired);

        Camera(12); // loading screen — still gated
        Assert.DoesNotContain(Checkpoint.OnWorldLoad, _fired);

        Camera(2); // in cockpit
        Assert.Contains(Checkpoint.OnWorldLoad, _fired);
        Assert.Contains(Checkpoint.OnEnterCockpit, _fired);
        // World load fires before cockpit entry.
        Assert.True(_fired.IndexOf(Checkpoint.OnWorldLoad) < _fired.IndexOf(Checkpoint.OnEnterCockpit));
    }

    [Fact]
    public void FlightLoaded_WhileAlreadyInFlightCamera_FiresImmediately()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        Camera(9); // external in-flight camera
        _sm.Handle(new SimStateEvent(SimStateEventKind.FlightLoaded));
        Assert.Contains(Checkpoint.OnWorldLoad, _fired);
    }

    [Fact]
    public void CameraFlicker_WithinDebounce_DoesNotFire()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        Camera(11);

        // Flicker: 11 -> 2 -> 11 inside the debounce window.
        _sm.Handle(new SimStateEvent(SimStateEventKind.CameraState, 2));
        _time.Advance(TimeSpan.FromSeconds(1));
        _sm.Handle(new SimStateEvent(SimStateEventKind.CameraState, 11));
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(Checkpoint.OnEnterCockpit, _fired);
    }

    [Fact]
    public void EnterCockpit_FiresAfterDebounce()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.CameraState, 2));
        _time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.DoesNotContain(Checkpoint.OnEnterCockpit, _fired); // not yet committed
        _time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Contains(Checkpoint.OnEnterCockpit, _fired);
    }

    [Fact]
    public void ExitFlight_FiresOnReturnToMenu_AndReArmsPerFlightCheckpoints()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.FlightLoaded));
        Camera(2); // world load + cockpit
        Camera(11); // back to menu

        Assert.Contains(Checkpoint.OnExitFlight, _fired);

        // Second flight: world load and cockpit fire again.
        _fired.Clear();
        _sm.Handle(new SimStateEvent(SimStateEventKind.FlightLoaded));
        Camera(2);
        Assert.Contains(Checkpoint.OnWorldLoad, _fired);
        Assert.Contains(Checkpoint.OnEnterCockpit, _fired);
    }

    [Fact]
    public void MenuCamera_BeforeAnyFlight_DoesNotFireExitFlight()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        Camera(11);
        Assert.DoesNotContain(Checkpoint.OnExitFlight, _fired);
    }

    [Fact]
    public void TransientDisconnect_WithinGrace_DoesNotTearDown()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionLost));
        _time.Advance(TimeSpan.FromSeconds(29));
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _time.Advance(TimeSpan.FromSeconds(120));

        Assert.DoesNotContain(Checkpoint.OnSimExit, _fired);
        Assert.True(_sm.IsSessionActive);
    }

    [Fact]
    public void Disconnect_BeyondGrace_FiresOnSimExit()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionLost));
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.Contains(Checkpoint.OnSimExit, _fired);
        Assert.False(_sm.IsSessionActive);
    }

    [Fact]
    public void QuitEvent_FiresOnSimExit_Immediately_AndOnlyOnce()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.Quit));
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionLost));
        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(1, _fired.Count(c => c == Checkpoint.OnSimExit));
    }

    [Fact]
    public void AfterSimExit_NothingElseFires()
    {
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.Quit));
        _fired.Clear();

        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.FlightLoaded));
        Camera(2);

        Assert.Empty(_fired);
    }

    [Fact]
    public void ManualStop_FiresOnSimExit()
    {
        _sm.StartSession();
        _sm.StopSession();
        Assert.Contains(Checkpoint.OnSimExit, _fired);
    }

    [Fact]
    public void StartSession_WhenSimAlreadyConnected_FiresOnSimStartImmediately()
    {
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.StartSession();
        Assert.Equal(new[] { Checkpoint.LauncherStart, Checkpoint.OnSimStart }, _fired);
    }

    [Fact]
    public void ZeroDebounce_CommitsImmediately()
    {
        _sm.Configure(new SimConnectionConfig { DebounceSeconds = 0 });
        _sm.StartSession();
        _sm.Handle(new SimStateEvent(SimStateEventKind.ConnectionOpened));
        _sm.Handle(new SimStateEvent(SimStateEventKind.CameraState, 2));
        Assert.Contains(Checkpoint.OnEnterCockpit, _fired);
    }
}
