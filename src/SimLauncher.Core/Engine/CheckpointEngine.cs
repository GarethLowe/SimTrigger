using Microsoft.Extensions.Logging;
using SimLauncher.Core.Config;
using SimLauncher.Core.Processes;

namespace SimLauncher.Core.Engine;

/// <summary>
/// Launches and shuts down the managed apps of the active profile as checkpoints fire.
///
///  - Delays are cancellable: teardown or Exit Flight aborts pending countdowns.
///  - waitForApp gives deterministic ordering within a checkpoint.
///  - Re-fired checkpoints (after Exit Flight) do not relaunch apps that are still running.
///  - Teardown shuts everything down per shutdown mode; Leave and adopted apps survive.
/// </summary>
public sealed class CheckpointEngine : IDisposable
{
    private static readonly TimeSpan DependencyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IProcessManager _procs;
    private readonly TimeProvider _time;
    private readonly ILogger<CheckpointEngine> _log;
    private readonly object _gate = new();

    private List<ManagedApp> _apps = new();
    private CancellationTokenSource? _sessionCts;
    private bool _sessionActive;
    private bool _tearingDown;

    public event Action<ManagedApp>? AppChanged;

    public CheckpointEngine(IProcessManager procs, ILogger<CheckpointEngine> log, TimeProvider? time = null)
    {
        _procs = procs;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ManagedApp> Apps
    {
        get
        {
            lock (_gate)
            {
                return _apps.ToList();
            }
        }
    }

    public bool IsSessionActive
    {
        get
        {
            lock (_gate)
            {
                return _sessionActive;
            }
        }
    }

    /// <summary>Rebuilds the managed-app list from a profile. Ignored while a session is active.</summary>
    public bool LoadProfile(ProfileConfig profile)
    {
        lock (_gate)
        {
            if (_sessionActive)
            {
                _log.LogWarning("Profile change ignored while a session is active");
                return false;
            }
            _apps = profile.Apps.Select(a => new ManagedApp(a)).ToList();
        }
        NotifyAll();
        return true;
    }

    /// <summary>Arms all apps for a new session. Call before the first checkpoint fires.</summary>
    public void ArmSession()
    {
        lock (_gate)
        {
            if (_sessionActive)
            {
                return;
            }
            _sessionActive = true;
            _tearingDown = false;
            _sessionCts = new CancellationTokenSource();
            foreach (var app in _apps)
            {
                app.Status = AppStatus.Waiting;
                app.IsAdopted = false;
                app.Pid = null;
                app.StartedAt = null;
                app.CountdownEndsAt = null;
                app.LastError = null;
                app.RestartAttempts = 0;
                app.Process = null;
                app.ShutdownRequested = false;
            }
        }
        NotifyAll();
    }

    /// <summary>
    /// Reacts to a checkpoint. OnSimExit performs teardown (and then launches any apps
    /// assigned to the OnSimExit section). OnExitFlight first cancels pending countdowns
    /// from the per-flight checkpoints, then launches its own apps.
    /// </summary>
    public async Task OnCheckpointAsync(Checkpoint checkpoint)
    {
        List<ManagedApp> toLaunch;
        lock (_gate)
        {
            if (!_sessionActive)
            {
                return;
            }
            toLaunch = _apps.Where(a => a.Config.Checkpoint == checkpoint).ToList();
        }

        if (checkpoint == Checkpoint.OnSimExit)
        {
            await TeardownAsync().ConfigureAwait(false);
            // Post-session apps (e.g. a logbook exporter) run after teardown, unmanaged
            // from here on: the session is over, so nothing will ever shut them down.
            foreach (var app in toLaunch)
            {
                _ = LaunchAsync(app, CancellationToken.None);
            }
            return;
        }

        if (checkpoint == Checkpoint.OnExitFlight)
        {
            CancelPendingPerFlightLaunches();
        }

        foreach (var app in toLaunch)
        {
            if (app.IsRunning || app.Status is AppStatus.Starting or AppStatus.Delayed or AppStatus.WaitingForDependency)
            {
                _log.LogDebug("{App} already {Status}; not relaunching on {Checkpoint}", app.Name, app.Status, checkpoint);
                continue;
            }
            // Token captured per launch: OnExitFlight rotates the session token while
            // cancelling pending per-flight countdowns, and its own apps must get the
            // fresh (uncancelled) token.
            _ = LaunchAsync(app, CurrentSessionToken());
        }
    }

    private Task? _teardownTask;

    /// <summary>
    /// Shuts down every managed app per its shutdown mode and ends the session.
    /// Idempotent and joinable: concurrent callers all await the same in-flight
    /// teardown, so an exit path can reliably wait for apps to be closed.
    /// </summary>
    public Task TeardownAsync()
    {
        lock (_gate)
        {
            if (_tearingDown)
            {
                return _teardownTask ?? Task.CompletedTask;
            }
            if (!_sessionActive)
            {
                return Task.CompletedTask;
            }
            _tearingDown = true;
            _teardownTask = Task.Run(TeardownCoreAsync);
            return _teardownTask;
        }
    }

    private async Task TeardownCoreAsync()
    {
        List<ManagedApp> running;
        lock (_gate)
        {
            _sessionCts?.Cancel();
            running = _apps.Where(a => a.Process is not null && !a.Process.HasExited).ToList();

            // Anything still counting down or waiting goes back to idle.
            foreach (var app in _apps.Where(a =>
                a.Status is AppStatus.Delayed or AppStatus.WaitingForDependency or AppStatus.Waiting))
            {
                app.Status = AppStatus.Idle;
                app.CountdownEndsAt = null;
            }
        }
        NotifyAll();

        _log.LogInformation("Teardown: shutting down {Count} running app(s)", running.Count);
        await Task.WhenAll(running.Select(ShutdownOneAsync)).ConfigureAwait(false);

        lock (_gate)
        {
            _sessionActive = false;
            _tearingDown = false;
            _teardownTask = null;
            _sessionCts?.Dispose();
            _sessionCts = null;
        }
        NotifyAll();
    }

    private async Task ShutdownOneAsync(ManagedApp app)
    {
        IManagedProcess? process;
        ShutdownMode mode;
        lock (_gate)
        {
            process = app.Process;
            mode = app.Config.EffectiveShutdown(app.IsAdopted);
            app.ShutdownRequested = true;
        }
        if (process is null)
        {
            return;
        }

        switch (mode)
        {
            case ShutdownMode.Leave:
                _log.LogInformation("Leaving {App} (pid {Pid}) running", app.Name, app.Pid);
                lock (_gate)
                {
                    app.Status = AppStatus.Idle;
                }
                Notify(app);
                return;

            case ShutdownMode.Kill:
                _log.LogInformation("Killing {App} (pid {Pid})", app.Name, app.Pid);
                process.Kill();
                break;

            case ShutdownMode.Graceful:
                _log.LogInformation("Gracefully closing {App} (pid {Pid}), timeout {Timeout}s",
                    app.Name, app.Pid, app.Config.ShutdownTimeoutSeconds);
                var closed = process.TryCloseMainWindow();
                if (!closed)
                {
                    _log.LogDebug("{App}: CloseMainWindow not delivered; will wait then kill", app.Name);
                }
                var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(app.Config.ShutdownTimeoutSeconds);
                while (!process.HasExited && _time.GetUtcNow() < deadline)
                {
                    await Task.Delay(ExitPollInterval, _time).ConfigureAwait(false);
                }
                if (!process.HasExited)
                {
                    _log.LogWarning("{App} did not close within {Timeout}s; killing", app.Name, app.Config.ShutdownTimeoutSeconds);
                    process.Kill();
                }
                break;
        }

        lock (_gate)
        {
            app.Status = AppStatus.Exited;
        }
        Notify(app);
    }

    private void CancelPendingPerFlightLaunches()
    {
        List<ManagedApp> cancelled = new();
        lock (_gate)
        {
            foreach (var app in _apps)
            {
                if (app.Config.Checkpoint is Checkpoint.OnWorldLoad or Checkpoint.OnEnterCockpit
                    && app.Status is AppStatus.Delayed or AppStatus.WaitingForDependency)
                {
                    app.Status = AppStatus.Waiting;
                    app.CountdownEndsAt = null;
                    cancelled.Add(app);
                }
            }
        }
        foreach (var app in cancelled)
        {
            _log.LogInformation("{App}: pending launch aborted by Exit Flight", app.Name);
            Notify(app);
        }
        if (cancelled.Count > 0)
        {
            RotateSessionToken();
        }
    }

    /// <summary>
    /// Replaces the session CTS so in-flight delay/dependency waits observe cancellation
    /// while future launches (this session) still get a live token.
    /// </summary>
    private void RotateSessionToken()
    {
        lock (_gate)
        {
            if (!_sessionActive || _tearingDown)
            {
                return;
            }
            var old = _sessionCts;
            _sessionCts = new CancellationTokenSource();
            old?.Cancel();
            old?.Dispose();
        }
    }

    private CancellationToken CurrentSessionToken()
    {
        lock (_gate)
        {
            return _sessionCts?.Token ?? new CancellationToken(canceled: true);
        }
    }

    private async Task LaunchAsync(ManagedApp app, CancellationToken ct)
    {
        try
        {
            // 1. Optional delay, cancellable.
            if (app.Config.DelaySeconds > 0)
            {
                lock (_gate)
                {
                    app.Status = AppStatus.Delayed;
                    app.CountdownEndsAt = _time.GetUtcNow() + TimeSpan.FromSeconds(app.Config.DelaySeconds);
                }
                Notify(app);
                await Task.Delay(TimeSpan.FromSeconds(app.Config.DelaySeconds), _time, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    app.CountdownEndsAt = null;
                }
            }

            // 2. Optional dependency ordering within the checkpoint.
            if (!string.IsNullOrEmpty(app.Config.WaitForApp))
            {
                lock (_gate)
                {
                    app.Status = AppStatus.WaitingForDependency;
                }
                Notify(app);
                await WaitForDependencyAsync(app, ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();

            // 3. Already-running detection.
            var existing = _procs.FindExisting(app.Config.Path);
            if (existing is not null)
            {
                switch (app.Config.AlreadyRunning)
                {
                    case AlreadyRunningBehavior.Skip:
                        _log.LogInformation("{App} is already running (pid {Pid}); skipping", app.Name, existing.Pid);
                        lock (_gate)
                        {
                            app.Status = AppStatus.Skipped;
                            app.Pid = existing.Pid;
                        }
                        Notify(app);
                        return;

                    case AlreadyRunningBehavior.Adopt:
                        _log.LogInformation("{App} is already running (pid {Pid}); adopting", app.Name, existing.Pid);
                        AttachProcess(app, existing, adopted: true);
                        return;

                    case AlreadyRunningBehavior.StartAnother:
                        _log.LogInformation("{App} is already running; starting another instance", app.Name);
                        break;
                }
            }

            // 4. Start it.
            lock (_gate)
            {
                app.Status = AppStatus.Starting;
            }
            Notify(app);

            var spec = new ProcessStartSpec(app.Config.Path, app.Config.Args, app.Config.EffectiveShellExecute,
                app.Config.RunAsAdmin);
            var process = _procs.Start(spec);

            if (process is null)
            {
                // Shell/URI hand-off (e.g. MSFS via steam:// or shell:AppsFolder). The app
                // is presumably starting; we just have no handle to manage. Treat as
                // running-unmanaged so the UI reflects reality.
                _log.LogInformation("{App}: launched via shell hand-off; no process handle to manage", app.Name);
                lock (_gate)
                {
                    app.Status = AppStatus.Running;
                    app.StartedAt = _time.GetUtcNow();
                }
                Notify(app);
                return;
            }

            AttachProcess(app, process, adopted: false);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("{App}: pending launch cancelled", app.Name);
            lock (_gate)
            {
                if (app.Status is AppStatus.Delayed or AppStatus.WaitingForDependency or AppStatus.Starting)
                {
                    app.Status = _tearingDown ? AppStatus.Idle : AppStatus.Waiting;
                    app.CountdownEndsAt = null;
                }
            }
            Notify(app);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{App}: failed to launch from {Path}", app.Name, app.Config.Path);
            lock (_gate)
            {
                app.Status = AppStatus.Failed;
                app.LastError = ex.Message;
                app.CountdownEndsAt = null;
            }
            Notify(app);
        }
    }

    private async Task WaitForDependencyAsync(ManagedApp app, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            ManagedApp? dep;
            lock (_gate)
            {
                dep = _apps.FirstOrDefault(a =>
                    string.Equals(a.Name, app.Config.WaitForApp, StringComparison.OrdinalIgnoreCase)
                    && a.Config.Checkpoint == app.Config.Checkpoint);
            }
            if (dep is null)
            {
                _log.LogWarning("{App}: waitForApp '{Dep}' not found at this checkpoint; launching anyway",
                    app.Name, app.Config.WaitForApp);
                return;
            }

            switch (dep.Status)
            {
                case AppStatus.Running:
                case AppStatus.Skipped: // skipped-because-already-running counts as available
                    var readyAt = (dep.StartedAt ?? _time.GetUtcNow())
                        + TimeSpan.FromSeconds(app.Config.WaitForAppReadySeconds);
                    var wait = readyAt - _time.GetUtcNow();
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                    }
                    return;

                case AppStatus.Failed:
                    _log.LogWarning("{App}: dependency '{Dep}' failed; launching anyway", app.Name, dep.Name);
                    return;
            }

            await Task.Delay(DependencyPollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    private void AttachProcess(ManagedApp app, IManagedProcess process, bool adopted)
    {
        lock (_gate)
        {
            app.Process = process;
            app.Pid = process.Pid;
            app.StartedAt = adopted ? process.StartedAt : _time.GetUtcNow();
            app.IsAdopted = adopted;
            app.Status = AppStatus.Running;
            app.ShutdownRequested = false;
        }
        process.Exited += (_, _) => OnProcessExited(app, process);
        // The process may have died between start and subscription.
        if (process.HasExited)
        {
            OnProcessExited(app, process);
            return;
        }
        _log.LogInformation("{App} {Verb} (pid {Pid})", app.Name, adopted ? "adopted" : "started", process.Pid);
        Notify(app);
    }

    private void OnProcessExited(ManagedApp app, IManagedProcess process)
    {
        bool unexpected;
        lock (_gate)
        {
            if (app.Process != process || app.Status != AppStatus.Running)
            {
                return; // stale handle or already handled
            }
            app.Status = AppStatus.Exited;
            unexpected = !app.ShutdownRequested && _sessionActive && !_tearingDown;
        }
        _log.LogInformation("{App} (pid {Pid}) exited{Unexpected}", app.Name, process.Pid,
            unexpected ? " unexpectedly" : "");
        Notify(app);

        if (unexpected && app.Config.RestartIfCrashed)
        {
            _ = RestartCrashedAsync(app);
        }
    }

    private async Task RestartCrashedAsync(ManagedApp app)
    {
        int attempt;
        lock (_gate)
        {
            if (!_sessionActive || _tearingDown || app.RestartAttempts >= 3)
            {
                if (app.RestartAttempts >= 3)
                {
                    _log.LogWarning("{App}: giving up after 3 restart attempts", app.Name);
                    app.Status = AppStatus.Failed;
                    app.LastError = "Crashed repeatedly; gave up after 3 restarts.";
                }
                Notify(app);
                return;
            }
            attempt = ++app.RestartAttempts;
        }

        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
        _log.LogInformation("{App}: restart attempt {Attempt}/3 in {Backoff}s", app.Name, attempt, backoff.TotalSeconds);
        try
        {
            await Task.Delay(backoff, _time, CurrentSessionToken()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await LaunchCoreForRestartAsync(app).ConfigureAwait(false);
    }

    private async Task LaunchCoreForRestartAsync(ManagedApp app)
    {
        // Bypass delay/dependency on restart — those already ran this session.
        var ct = CurrentSessionToken();
        if (ct.IsCancellationRequested)
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                app.Status = AppStatus.Starting;
            }
            Notify(app);
            var process = _procs.Start(new ProcessStartSpec(
                app.Config.Path, app.Config.Args, app.Config.EffectiveShellExecute,
                app.Config.RunAsAdmin));
            if (process is null)
            {
                lock (_gate)
                {
                    app.Status = AppStatus.Running;
                    app.StartedAt = _time.GetUtcNow();
                }
                Notify(app);
                return;
            }
            AttachProcess(app, process, adopted: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{App}: restart failed", app.Name);
            lock (_gate)
            {
                app.Status = AppStatus.Failed;
                app.LastError = ex.Message;
            }
            Notify(app);
        }
    }

    private void Notify(ManagedApp app) => AppChanged?.Invoke(app);

    private void NotifyAll()
    {
        foreach (var app in Apps)
        {
            Notify(app);
        }
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
    }
}
