namespace SimLauncher.Core.Processes;

/// <param name="RunAsAdmin">Launch elevated (ShellExecute with the "runas" verb → UAC prompt).</param>
public sealed record ProcessStartSpec(
    string Path,
    string Args,
    bool UseShellExecute,
    bool RunAsAdmin = false);

/// <summary>A process handle the engine can observe and shut down. Fakeable for tests.</summary>
public interface IManagedProcess
{
    int Pid { get; }
    bool HasExited { get; }
    DateTimeOffset StartedAt { get; }

    /// <summary>Raised once when the process exits. May fire on any thread.</summary>
    event EventHandler? Exited;

    /// <summary>Requests a graceful close (WM_CLOSE to the main window). False if it could not be delivered.</summary>
    bool TryCloseMainWindow();

    void Kill();
}

public interface IProcessManager
{
    /// <summary>
    /// Starts a process. Returns null when the start produced no observable process,
    /// e.g. URI/shell launches that hand off to a protocol handler — the app may still
    /// be starting, we just cannot manage its handle.
    /// </summary>
    IManagedProcess? Start(ProcessStartSpec spec);

    /// <summary>Finds an already-running instance by executable path (matched on process name). Null if none.</summary>
    IManagedProcess? FindExisting(string path);

    /// <summary>True when a process with the given name (no extension) is running. Used for MSFS detection.</summary>
    bool IsProcessRunning(string processName);
}
