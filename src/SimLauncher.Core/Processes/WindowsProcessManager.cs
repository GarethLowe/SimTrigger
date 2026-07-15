using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace SimLauncher.Core.Processes;

/// <summary>
/// Real process manager. Launched processes are deliberately NOT tied to SimLauncher's
/// lifetime: if SimLauncher exits or dies, managed apps are orphaned and keep running.
/// Teardown only happens through the session's shutdown flow (per-app ShutdownMode).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessManager : IProcessManager
{
    private readonly ILogger<WindowsProcessManager> _log;

    public WindowsProcessManager(ILogger<WindowsProcessManager> log)
    {
        _log = log;
    }

    public IManagedProcess? Start(ProcessStartSpec spec)
    {
        Process? process;
        try
        {
            process = Process.Start(BuildStartInfo(spec, elevated: spec.RunAsAdmin));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && !spec.RunAsAdmin)
        {
            // The exe's manifest demands administrator (e.g. REX Atmos Core): a plain
            // CreateProcess fails with error 740. Relaunch via the shell with the
            // "runas" verb so the user gets a UAC prompt instead of an app error.
            _log.LogInformation("{Path} requires elevation; retrying via UAC", spec.Path);
            process = Process.Start(BuildStartInfo(spec, elevated: true));
        }

        if (process is null)
        {
            _log.LogInformation("Start of {Path} returned no process handle (shell/URI hand-off)", spec.Path);
            return null;
        }

        return new RealManagedProcess(process);
    }

    private const int ErrorElevationRequired = 740;

    private static ProcessStartInfo BuildStartInfo(ProcessStartSpec spec, bool elevated)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.Path,
            Arguments = spec.Args,
            // Elevation is only possible through ShellExecute.
            UseShellExecute = spec.UseShellExecute || elevated,
        };
        if (elevated)
        {
            psi.Verb = "runas";
        }
        // Only meaningful for real exe paths (not shell:/steam: URIs).
        if (File.Exists(spec.Path))
        {
            var dir = Path.GetDirectoryName(spec.Path);
            if (!string.IsNullOrEmpty(dir))
            {
                psi.WorkingDirectory = dir;
            }
        }
        return psi;
    }

    public IManagedProcess? FindExisting(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name) || path.Contains("://", StringComparison.Ordinal))
        {
            return null; // URIs have no meaningful process name
        }

        foreach (var candidate in Process.GetProcessesByName(name))
        {
            try
            {
                if (!candidate.HasExited)
                {
                    return new RealManagedProcess(candidate);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Access denied / exited between enumeration and inspection.
            }
            candidate.Dispose();
        }
        return null;
    }

    public bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var found = false;
        foreach (var p in processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    found = true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                found = true; // access denied usually means it exists (elevated process)
            }
            p.Dispose();
        }
        return found;
    }

    private sealed class RealManagedProcess : IManagedProcess
    {
        private readonly Process _process;

        public RealManagedProcess(Process process)
        {
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) => Exited?.Invoke(this, EventArgs.Empty);
            StartedAt = DateTimeOffset.Now;
            try
            {
                StartedAt = new DateTimeOffset(_process.StartTime);
            }
            catch
            {
                // StartTime can throw for protected processes; fall back to 'now'.
            }
        }

        public int Pid => _process.Id;
        public DateTimeOffset StartedAt { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public event EventHandler? Exited;

        public bool TryCloseMainWindow()
        {
            try
            {
                return _process.CloseMainWindow();
            }
            catch
            {
                return false;
            }
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
        }
    }

}
