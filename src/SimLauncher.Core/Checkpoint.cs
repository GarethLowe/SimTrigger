namespace SimLauncher.Core;

/// <summary>
/// The linear checkpoint timeline of a sim session. <see cref="LauncherStart"/> is the
/// special pre-sim checkpoint fired when the user clicks Start Session (MSFS itself and
/// alongside-MSFS apps live here). The remaining five map to the UI sections.
/// </summary>
public enum Checkpoint
{
    LauncherStart,
    OnSimStart,
    OnWorldLoad,
    OnEnterCockpit,
    OnExitFlight,
    OnSimExit,
}

public static class CheckpointInfo
{
    public static string DisplayName(this Checkpoint cp) => cp switch
    {
        Checkpoint.LauncherStart => "On Session Start",
        Checkpoint.OnSimStart => "On Sim Start",
        Checkpoint.OnWorldLoad => "On World Load / Free Flight Start",
        Checkpoint.OnEnterCockpit => "On Enter Cockpit",
        Checkpoint.OnExitFlight => "On Exit Flight",
        Checkpoint.OnSimExit => "On Sim Exit",
        _ => cp.ToString(),
    };
}
