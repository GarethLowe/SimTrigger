using SimLauncher.Core.Config;
using SimLauncher.Core.Processes;

namespace SimLauncher.Core.Engine;

public enum AppStatus
{
    /// <summary>Not part of an active session.</summary>
    Idle,
    /// <summary>Session active; this app's checkpoint has not fired yet.</summary>
    Waiting,
    /// <summary>Checkpoint fired; delay countdown running.</summary>
    Delayed,
    /// <summary>Waiting for its waitForApp dependency to be ready.</summary>
    WaitingForDependency,
    Starting,
    Running,
    Exited,
    Failed,
    /// <summary>Already running at launch time and configured to skip.</summary>
    Skipped,
}

/// <summary>Runtime state for one configured app within the active profile.</summary>
public sealed class ManagedApp
{
    public ManagedApp(AppConfig config)
    {
        Config = config;
    }

    public AppConfig Config { get; internal set; }
    public AppStatus Status { get; internal set; } = AppStatus.Idle;

    /// <summary>True when we attached to a pre-existing instance instead of launching one.</summary>
    public bool IsAdopted { get; internal set; }

    public int? Pid { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>When the delay countdown completes; set while Status is Delayed.</summary>
    public DateTimeOffset? CountdownEndsAt { get; internal set; }

    public string? LastError { get; internal set; }
    public int RestartAttempts { get; internal set; }

    internal IManagedProcess? Process { get; set; }

    /// <summary>Guards against the crash-restart logic reacting to our own shutdown.</summary>
    internal bool ShutdownRequested { get; set; }

    public string Name => Config.Name;
    public bool IsRunning => Status == AppStatus.Running;
}
