using Microsoft.Extensions.Time.Testing;
using SimLauncher.Traffic;
using Xunit;

namespace SimLauncher.Traffic.Tests;

public class AutoCullerTests
{
    private static TrafficAircraft Ac(string callsign, double lat, double alt, double heading = 0)
        => new() { Callsign = callsign, Lat = lat, Lon = 0, Alt = alt, Heading = heading, InSim = true };

    private static ConflictPair ConflictOf(TrafficAircraft a, TrafficAircraft b)
        => new(a, b, 2.0, 500, 120, ConflictSeverity.Conflict);

    private static (AutoCuller Culler, FakeTimeProvider Time, ConflictPair Pair) Setup(bool enabled = true, bool dryRun = false)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var culler = new AutoCuller(time)
        {
            Options = new AutoCullOptions { Enabled = enabled, DryRun = dryRun, SustainSeconds = 2, CooldownSeconds = 120 },
        };
        // CHASE trails LEAD (both northbound, CHASE south of LEAD) → CHASE is the intruder.
        var pair = ConflictOf(Ac("LEAD", 53.05, 3000), Ac("CHASE", 53.00, 3000));
        return (culler, time, pair);
    }

    [Fact]
    public void FiresOnlyAfterSustainedConflict()
    {
        var (culler, time, pair) = Setup();

        Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));   // first sighting
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));   // not sustained yet

        time.Advance(TimeSpan.FromSeconds(1.5));
        var decision = Assert.Single(culler.Evaluate(new[] { pair }, _ => true));
        Assert.Equal("CHASE", decision.TargetCallsign);
        Assert.False(decision.DryRun);
    }

    [Fact]
    public void CooldownPreventsRefiringOnTheSameCallsign()
    {
        var (culler, time, pair) = Setup();
        culler.Evaluate(new[] { pair }, _ => true);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(culler.Evaluate(new[] { pair }, _ => true));

        // Still conflicting (e.g. dry-run left it alive): must not fire again inside cooldown.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));
    }

    [Fact]
    public void DryRunFlagPropagates()
    {
        var (culler, time, pair) = Setup(dryRun: true);
        culler.Evaluate(new[] { pair }, _ => true);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(Assert.Single(culler.Evaluate(new[] { pair }, _ => true)).DryRun);
    }

    [Fact]
    public void DisabledCullerNeverFires()
    {
        var (culler, time, pair) = Setup(enabled: false);
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));
            time.Advance(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void SuppressedCullerNeverFires()
    {
        var (culler, time, pair) = Setup();
        culler.Suppressed = true;
        for (var i = 0; i < 5; i++)
        {
            Assert.Empty(culler.Evaluate(new[] { pair }, _ => true));
            time.Advance(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void TargetMustStillExistInFeed()
    {
        var (culler, time, pair) = Setup();
        culler.Evaluate(new[] { pair }, _ => true);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(culler.Evaluate(new[] { pair }, _ => false));
    }

    [Fact]
    public void CautionsNeverTriggerCulls()
    {
        var (culler, time, _) = Setup();
        var caution = new ConflictPair(Ac("A", 53.05, 3000), Ac("B", 53.00, 3000),
            4.0, 1200, 60, ConflictSeverity.Caution);
        culler.Evaluate(new[] { caution }, _ => true);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(culler.Evaluate(new[] { caution }, _ => true));
    }

    [Fact]
    public void ConflictMustReSustainAfterClearing()
    {
        var (culler, time, pair) = Setup();
        culler.Evaluate(new[] { pair }, _ => true);          // starts the clock
        time.Advance(TimeSpan.FromSeconds(1));
        culler.Evaluate(Array.Empty<ConflictPair>(), _ => true);  // conflict cleared

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(culler.Evaluate(new[] { pair }, _ => true)); // clock restarted
    }
}
