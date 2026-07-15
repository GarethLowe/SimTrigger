using SimLauncher.Core;
using SimLauncher.Core.Processes;

namespace SimLauncher.Core.Tests;

public sealed class FakeSimStateSource : ISimStateSource
{
    public event EventHandler<SimStateEvent>? StateEvent;
    public event EventHandler<SimTelemetry>? TelemetryReceived;
    public bool IsConnected { get; set; }
    public bool Started { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Started = false;
        return Task.CompletedTask;
    }

    public void Raise(SimStateEventKind kind, double value = 0)
    {
        if (kind == SimStateEventKind.ConnectionOpened)
        {
            IsConnected = true;
        }
        if (kind is SimStateEventKind.ConnectionLost or SimStateEventKind.Quit)
        {
            IsConnected = false;
        }
        StateEvent?.Invoke(this, new SimStateEvent(kind, value));
    }

    public void RaiseTelemetry(SimTelemetry telemetry)
        => TelemetryReceived?.Invoke(this, telemetry);

    public void Dispose() { }
}

public sealed class FakeProcess : IManagedProcess
{
    public FakeProcess(int pid)
    {
        Pid = pid;
    }

    public int Pid { get; }
    public bool HasExited { get; private set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UnixEpoch;

    /// <summary>When true (default), TryCloseMainWindow immediately exits the process.</summary>
    public bool RespondsToClose { get; set; } = true;
    public bool CloseRequested { get; private set; }
    public bool Killed { get; private set; }

    public event EventHandler? Exited;

    public bool TryCloseMainWindow()
    {
        CloseRequested = true;
        if (RespondsToClose)
        {
            MarkExited();
        }
        return true;
    }

    public void Kill()
    {
        Killed = true;
        MarkExited();
    }

    /// <summary>Simulates the process dying on its own (crash).</summary>
    public void MarkExited()
    {
        if (HasExited)
        {
            return;
        }
        HasExited = true;
        Exited?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FakeProcessManager : IProcessManager
{
    private int _nextPid = 1000;

    public List<(ProcessStartSpec Spec, FakeProcess Process)> Starts { get; } = new();
    public Dictionary<string, FakeProcess> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<ProcessStartSpec, bool>? ReturnNullFor { get; set; }

    public FakeProcess? LastProcess => Starts.Count > 0 ? Starts[^1].Process : null;

    public IManagedProcess? Start(ProcessStartSpec spec)
    {
        if (ReturnNullFor?.Invoke(spec) == true)
        {
            return null;
        }
        var process = new FakeProcess(_nextPid++);
        Starts.Add((spec, process));
        return process;
    }

    public IManagedProcess? FindExisting(string path)
        => Existing.TryGetValue(path, out var p) && !p.HasExited ? p : null;

    public HashSet<string> RunningProcessNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsProcessRunning(string processName) => RunningProcessNames.Contains(processName);
}
