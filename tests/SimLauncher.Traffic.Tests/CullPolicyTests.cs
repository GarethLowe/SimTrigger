using SimLauncher.Traffic;
using Xunit;

namespace SimLauncher.Traffic.Tests;

public class CullPolicyTests
{
    private static TrafficAircraft Ac(string callsign, double lat = 53.0, double lon = 0,
        double alt = 4000, double heading = 0, bool isPlayer = false, bool onGround = false,
        bool atDestination = false)
        => new()
        {
            Callsign = callsign,
            Lat = lat,
            Lon = lon,
            Alt = alt,
            Heading = heading,
            IsPlayer = isPlayer,
            OnGround = onGround,
            AtDestination = atDestination,
            InSim = true,
        };

    private static ConflictPair Pair(TrafficAircraft a, TrafficAircraft b)
        => new(a, b, 2.0, 500, 100, ConflictSeverity.Conflict);

    [Fact]
    public void NeverSelectsThePlayer()
    {
        var player = Ac("ME", isPlayer: true);
        var ai = Ac("AI1", lat: 53.02);
        Assert.Equal("AI1", CullPolicy.SelectTarget(Pair(player, ai))!.Callsign);
        Assert.Equal("AI1", CullPolicy.SelectTarget(Pair(ai, player))!.Callsign);
    }

    [Fact]
    public void ReturnsNullWhenNoValidTarget()
    {
        var player = Ac("ME", isPlayer: true);
        var grounded = Ac("AI1", onGround: true);
        Assert.Null(CullPolicy.SelectTarget(Pair(player, grounded)));
    }

    [Fact]
    public void PrefersTheTrailingAircraft()
    {
        // Both heading north; chaser is south of leader, so leader sits in its forward cone.
        var leader = Ac("LEAD", lat: 53.05, heading: 0, alt: 3000);
        var chaser = Ac("CHASE", lat: 53.00, heading: 0, alt: 3000);
        Assert.Equal("CHASE", CullPolicy.SelectTarget(Pair(leader, chaser))!.Callsign);
    }

    [Fact]
    public void PrefersTheHigherAircraftWhenNeitherTrails()
    {
        // Head-on: each has the other in its forward cone, so altitude decides.
        var low = Ac("LOW", lat: 53.00, heading: 0, alt: 3000);
        var high = Ac("HIGH", lat: 53.05, heading: 180, alt: 4500);
        Assert.Equal("HIGH", CullPolicy.SelectTarget(Pair(low, high))!.Callsign);
    }

    [Fact]
    public void SkipsGroundedAndArrivedAircraft()
    {
        var arrived = Ac("DONE", atDestination: true);
        var flying = Ac("FLY1", lat: 53.02);
        Assert.Equal("FLY1", CullPolicy.SelectTarget(Pair(arrived, flying))!.Callsign);
    }

    [Fact]
    public void SelectionIsDeterministic()
    {
        var a = Ac("AAA", lat: 53.00, heading: 90, alt: 3000);
        var b = Ac("BBB", lat: 53.05, heading: 270, alt: 3000);
        var first = CullPolicy.SelectTarget(Pair(a, b))!.Callsign;
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(first, CullPolicy.SelectTarget(Pair(a, b))!.Callsign);
            Assert.Equal(first, CullPolicy.SelectTarget(Pair(b, a))!.Callsign);
        }
    }
}
