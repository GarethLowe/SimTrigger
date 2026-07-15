using SimLauncher.Core;
using SimLauncher.Core.Config;
using Xunit;

namespace SimLauncher.Core.Tests;

public sealed class ConfigValidatorTests
{
    private static LauncherConfig Valid() => new()
    {
        ActiveProfile = "p",
        Profiles =
        {
            new ProfileConfig
            {
                Name = "p",
                Apps =
                {
                    new AppConfig { Name = "A", Path = @"C:\a.exe", Checkpoint = Checkpoint.OnSimStart },
                    new AppConfig { Name = "B", Path = @"C:\b.exe", Checkpoint = Checkpoint.OnSimStart, WaitForApp = "A" },
                },
            },
        },
    };

    [Fact]
    public void ValidConfig_HasNoErrors()
    {
        Assert.Empty(ConfigValidator.Validate(Valid()));
    }

    [Fact]
    public void MissingWaitForApp_IsReported()
    {
        var cfg = Valid();
        cfg.Profiles[0].Apps[1].WaitForApp = "Nope";
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("Nope"));
    }

    [Fact]
    public void WaitForApp_AcrossCheckpoints_IsReported()
    {
        var cfg = Valid();
        cfg.Profiles[0].Apps[0].Checkpoint = Checkpoint.OnWorldLoad;
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("different checkpoint"));
    }

    [Fact]
    public void WaitForApp_Cycle_IsReported()
    {
        var cfg = Valid();
        cfg.Profiles[0].Apps[0].WaitForApp = "B";
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("cycle"));
    }

    [Fact]
    public void DuplicateAppNames_AreReported()
    {
        var cfg = Valid();
        cfg.Profiles[0].Apps[1].Name = "A";
        cfg.Profiles[0].Apps[1].WaitForApp = null;
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("duplicate app name"));
    }

    [Fact]
    public void UnknownActiveProfile_IsReported()
    {
        var cfg = Valid();
        cfg.ActiveProfile = "ghost";
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("ghost"));
    }

    [Fact]
    public void NegativeDelay_IsReported()
    {
        var cfg = Valid();
        cfg.Profiles[0].Apps[0].DelaySeconds = -1;
        Assert.Contains(ConfigValidator.Validate(cfg), e => e.Contains("delaySeconds"));
    }
}
