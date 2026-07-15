namespace SimLauncher.Core.Config;

/// <summary>Builds the sample config shipped on first run.</summary>
public static class DefaultConfigFactory
{
    public static LauncherConfig Create() => new()
    {
        ActiveProfile = "Full IFR session",
        // MS Store install; Steam users: set path to steam://rungameid/2537590
        Msfs = new MsfsConfig(),
        Profiles =
        {
            new ProfileConfig
            {
                Name = "Full IFR session",
                Apps =
                {
                    new AppConfig
                    {
                        Name = "SPAD.neXt",
                        Path = @"C:\Program Files (x86)\SPAD.neXt\SPAD.neXt.exe",
                        Checkpoint = Checkpoint.LauncherStart,
                        DelaySeconds = 5,
                        Shutdown = ShutdownMode.Graceful,
                    },
                    new AppConfig
                    {
                        Name = "BeyondATC",
                        Path = @"C:\Program Files\BeyondATC\BeyondATC.exe",
                        Checkpoint = Checkpoint.OnSimStart,
                        Shutdown = ShutdownMode.Graceful,
                    },
                    new AppConfig
                    {
                        // REX must initialise after sim connect but before flight load —
                        // this ordering is the whole reason SimLauncher exists.
                        Name = "REX Atmos Core",
                        Path = @"C:\Program Files\REX\AtmosCore.exe",
                        Checkpoint = Checkpoint.OnSimStart,
                        Shutdown = ShutdownMode.Graceful,
                        RunAsAdmin = true, // REX's manifest demands administrator
                    },
                    new AppConfig
                    {
                        Name = "AutoFPS",
                        Path = @"C:\Program Files\AutoFPS\AutoFPS.exe",
                        Checkpoint = Checkpoint.OnWorldLoad,
                        Shutdown = ShutdownMode.Kill,
                    },
                },
            },
            new ProfileConfig
            {
                // Harmless smoke-test profile: three Notepads at successive checkpoints.
                Name = "Test (Notepad)",
                Apps =
                {
                    new AppConfig
                    {
                        Name = "Notepad (sim start)",
                        Path = @"C:\Windows\System32\notepad.exe",
                        Checkpoint = Checkpoint.OnSimStart,
                        AlreadyRunning = AlreadyRunningBehavior.StartAnother,
                    },
                    new AppConfig
                    {
                        Name = "Notepad (world load)",
                        Path = @"C:\Windows\System32\notepad.exe",
                        Checkpoint = Checkpoint.OnWorldLoad,
                        AlreadyRunning = AlreadyRunningBehavior.StartAnother,
                    },
                    new AppConfig
                    {
                        Name = "Notepad (cockpit)",
                        Path = @"C:\Windows\System32\notepad.exe",
                        Checkpoint = Checkpoint.OnEnterCockpit,
                        AlreadyRunning = AlreadyRunningBehavior.StartAnother,
                    },
                },
            },
        },
    };
}
