using System.Text.Json.Serialization;

namespace SimLauncher.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShutdownMode
{
    /// <summary>CloseMainWindow, wait shutdownTimeoutSeconds, then Kill.</summary>
    Graceful,
    /// <summary>Kill immediately on teardown.</summary>
    Kill,
    /// <summary>Never touch the process on teardown.</summary>
    Leave,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlreadyRunningBehavior
{
    /// <summary>Leave the existing instance alone and do not manage it.</summary>
    Skip,
    /// <summary>Manage the existing instance (treated as Leave on teardown unless shutdown is explicitly configured).</summary>
    Adopt,
    /// <summary>Start a second instance regardless.</summary>
    StartAnother,
}

public sealed class AppConfig
{
    public string Name { get; set; } = "";

    /// <summary>Exe path, or a URI / steam command (e.g. "steam://rungameid/2537590"). URIs are auto-launched via the shell.</summary>
    public string Path { get; set; } = "";

    public string Args { get; set; } = "";

    public Checkpoint Checkpoint { get; set; } = Checkpoint.OnSimStart;

    /// <summary>Seconds after the checkpoint fires before this app launches. Canonical unit is seconds.</summary>
    public int DelaySeconds { get; set; }

    /// <summary>Name of another app at the same checkpoint that must have started first.</summary>
    public string? WaitForApp { get; set; }

    /// <summary>Extra seconds after WaitForApp starts before this app is allowed to launch.</summary>
    public int WaitForAppReadySeconds { get; set; }

    /// <summary>Null means Graceful for launched processes and Leave for adopted ones.</summary>
    public ShutdownMode? Shutdown { get; set; }

    public int ShutdownTimeoutSeconds { get; set; } = 10;

    public bool RestartIfCrashed { get; set; }

    public AlreadyRunningBehavior AlreadyRunning { get; set; } = AlreadyRunningBehavior.Skip;

    /// <summary>Force ShellExecute-style start. Automatically true when Path looks like a URI.</summary>
    public bool ShellExecute { get; set; }

    /// <summary>
    /// Launch elevated via UAC (for apps whose manifest demands administrator, e.g. REX
    /// Atmos Core). Launch failures with ERROR_ELEVATION_REQUIRED also fall back to this
    /// automatically. Note: unless SimLauncher itself runs elevated, an elevated app
    /// cannot be closed or killed on teardown.
    /// </summary>
    public bool RunAsAdmin { get; set; }

    [JsonIgnore]
    public bool IsUriLaunch => Path.Contains("://", StringComparison.Ordinal);

    [JsonIgnore]
    public bool EffectiveShellExecute => ShellExecute || IsUriLaunch;

    public ShutdownMode EffectiveShutdown(bool adopted)
        => Shutdown ?? (adopted ? ShutdownMode.Leave : ShutdownMode.Graceful);
}

public sealed class ProfileConfig
{
    public string Name { get; set; } = "";
    public List<AppConfig> Apps { get; set; } = new();
}

public sealed class CameraStateMap
{
    // MSFS 2020 values; verify against MSFS 2024 with the debug panel before trusting them.
    public int MainMenu { get; set; } = 11;
    public int LoadingScreen { get; set; } = 12;
    public int CockpitMin { get; set; } = 2;
    public int CockpitMax { get; set; } = 6;
    public int FlightMin { get; set; } = 2;
    public int FlightMax { get; set; } = 10;

    public bool IsInFlight(double v) => v >= FlightMin && v <= FlightMax;
    public bool IsCockpit(double v) => v >= CockpitMin && v <= CockpitMax;
    public bool IsMainMenu(double v) => (int)v == MainMenu;
    public bool IsMenuOrLoading(double v) => (int)v == MainMenu || (int)v == LoadingScreen;
}

public sealed class SimConnectionConfig
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int DisconnectGraceSeconds { get; set; } = 30;
    public double DebounceSeconds { get; set; } = 2;
    public CameraStateMap CameraStates { get; set; } = new();
}

/// <summary>
/// How to launch MSFS itself. The sim is not a managed app: the Launch button starts it
/// and nothing ever shuts it down.
/// </summary>
public sealed class MsfsConfig
{
    /// <summary>Exe path, or URI/shell command. MS Store default; Steam: steam://rungameid/2537590.</summary>
    public string Path { get; set; } = @"shell:AppsFolder\Microsoft.Limitless_8wekyb3d8bbwe!App";

    public string Args { get; set; } = "";

    public bool ShellExecute { get; set; } = true;

    /// <summary>Process names used to detect a running sim (no extension).</summary>
    public List<string> ProcessNames { get; set; } = new() { "FlightSimulator2024", "FlightSimulator" };

    [JsonIgnore]
    public bool EffectiveShellExecute => ShellExecute || Path.Contains("://", StringComparison.Ordinal)
        || Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
}

public sealed class LauncherConfig
{
    public string ActiveProfile { get; set; } = "";
    public bool LaunchOnStartup { get; set; }

    /// <summary>Arm a session automatically when a running sim is detected (SimConnect accepts).</summary>
    public bool AutoStartSessionWhenSimDetected { get; set; } = true;

    /// <summary>Loopback HTTP control port (Stream Deck etc.). 0 disables the endpoint.</summary>
    public int LocalApiPort { get; set; } = 8731;

    public MsfsConfig Msfs { get; set; } = new();
    public SimConnectionConfig SimConnection { get; set; } = new();
    public List<ProfileConfig> Profiles { get; set; } = new();

    public ProfileConfig? FindActiveProfile()
        => Profiles.FirstOrDefault(p => p.Name == ActiveProfile) ?? Profiles.FirstOrDefault();
}
