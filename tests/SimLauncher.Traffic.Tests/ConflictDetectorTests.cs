using SimLauncher.Traffic;
using Xunit;

namespace SimLauncher.Traffic.Tests;

public class ConflictDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static TrafficAircraft Ac(string callsign, double lat, double lon, double alt,
        bool inSim = true, bool onGround = false, bool atDestination = false, bool isPlayer = false)
        => new()
        {
            Callsign = callsign,
            Lat = lat,
            Lon = lon,
            Alt = alt,
            InSim = inSim,
            OnGround = onGround,
            AtDestination = atDestination,
            IsPlayer = isPlayer,
        };

    // 1 minute of latitude = 1 nm, so lat offsets are easy to reason about at lon 0.

    [Fact]
    public void HaversineSanity()
    {
        Assert.Equal(60.0, GeoMath.HaversineNm(0, 0, 1, 0), 1);
        Assert.Equal(0.0, GeoMath.HaversineNm(53.42, -6.27, 53.42, -6.27), 5);
    }

    [Fact]
    public void ClosingPairInsideConflictThresholdsFlagsConflict()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };

        // Tick 1: 4 nm apart — establishes range history, nothing flagged yet.
        var tick1 = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 4.0 / 60, 0, 4800),
        }, T0);
        Assert.Empty(tick1.Conflicts);

        // Tick 2: 2 nm apart and 800 ft — closing, inside 3 nm / 1000 ft.
        var tick2 = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 2.0 / 60, 0, 4800),
        }, T0.AddSeconds(10));

        var pair = Assert.Single(tick2.Conflicts);
        Assert.Equal(ConflictSeverity.Conflict, pair.Severity);
        Assert.Equal(2.0, pair.HorizontalNm, 1);
        Assert.Equal(800, pair.VerticalFt, 0);
        Assert.True(pair.ClosureKnots > 0);
    }

    [Fact]
    public void ClosingPairInCautionBandFlagsCaution()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 4.8 / 60, 0, 5200) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 4.0 / 60, 0, 5200),
        }, T0.AddSeconds(10));

        Assert.Equal(ConflictSeverity.Caution, Assert.Single(result.Conflicts).Severity);
    }

    [Fact]
    public void DivergingPairIsNotFlagged()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 1.0 / 60, 0, 4000) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 2.0 / 60, 0, 4000),
        }, T0.AddSeconds(10));

        Assert.Empty(result.Conflicts);
        Assert.Equal(PairGate.NotClosing, Assert.Single(result.NearPairs).Gate);
    }

    [Fact]
    public void WideVerticalSeparationIsNotFlagged()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 3.0 / 60, 0, 8000) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 1.0 / 60, 0, 8000),
        }, T0.AddSeconds(10));

        Assert.Empty(result.Conflicts);
    }

    [Theory]
    [InlineData(false, false)] // not in sim
    [InlineData(true, true)]   // on ground
    public void IneligibleAircraftAreIgnored(bool inSim, bool onGround)
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 1.0 / 60, 0, 4000, inSim, onGround),
        }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 0.5 / 60, 0, 4000, inSim, onGround),
        }, T0.AddSeconds(10));

        Assert.Empty(result.Conflicts);
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal("B", exclusion.Aircraft.Callsign);
        Assert.NotEqual(ExclusionReasons.None, exclusion.Reasons);
    }

    [Fact]
    public void AirborneAtDestinationTrafficIsStillDetected()
    {
        // Regression: player on short final closing on an arrival flagged atDestination
        // (e.g. one sent around). atDestination must not blind the detector while airborne.
        var detector = new ConflictDetector(); // default PlayerVsAi scope
        detector.Evaluate(new[]
        {
            Ac("ME", 53.0, 0, 2000, inSim: false, isPlayer: true),
            Ac("AHEAD", 53.0 + 4.0 / 60, 0, 2400, atDestination: true),
        }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("ME", 53.0, 0, 2000, inSim: false, isPlayer: true),
            Ac("AHEAD", 53.0 + 2.0 / 60, 0, 2400, atDestination: true),
        }, T0.AddSeconds(10));

        Assert.Equal(ConflictSeverity.Conflict, Assert.Single(result.Conflicts).Severity);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void PlayerVsAiScopeIgnoresAiOnlyPairs()
    {
        var detector = new ConflictDetector(); // default scope
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 4.0 / 60, 0, 4000) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 2.0 / 60, 0, 4000),
        }, T0.AddSeconds(10));

        Assert.Empty(result.Conflicts);
        Assert.Empty(result.NearPairs); // scope-filtered pairs are not even traced
    }

    [Fact]
    public void AiVsAiScopeExcludesThePlayer()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.AiVsAi };
        detector.Evaluate(new[]
        {
            Ac("ME", 53.0, 0, 4000, isPlayer: true),
            Ac("B", 53.0 + 4.0 / 60, 0, 4000),
        }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("ME", 53.0, 0, 4000, isPlayer: true),
            Ac("B", 53.0 + 2.0 / 60, 0, 4000),
        }, T0.AddSeconds(10));

        Assert.Empty(result.Conflicts);
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal(ExclusionReasons.PlayerExcludedByScope, exclusion.Reasons);
    }

    [Fact]
    public void EnvelopeHistoryAllowsFlaggingOnFirstTickInsideCautionBand()
    {
        // History starts at the diagnostic envelope (2× caution = 10 nm), so a pair
        // that closes into the caution band is flagged the moment it crosses 5 nm.
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 8.0 / 60, 0, 4000) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 4.5 / 60, 0, 4000),
        }, T0.AddSeconds(30));

        Assert.Equal(ConflictSeverity.Caution, Assert.Single(result.Conflicts).Severity);
    }

    [Fact]
    public void PairMustReCloseAfterSeparatingBeyondEnvelope()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 2.0 / 60, 0, 4000) }, T0);

        // Separate beyond the diagnostic envelope (10 nm): pair history is forgotten.
        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 12.0 / 60, 0, 4000) }, T0.AddSeconds(10));

        // Back inside the band: first sighting again, so no closure info, no flag.
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 2.0 / 60, 0, 4000),
        }, T0.AddSeconds(20));
        Assert.Empty(result.Conflicts);
        Assert.Equal(PairGate.FirstSighting, Assert.Single(result.NearPairs).Gate);
    }

    [Fact]
    public void NearPairsTraceTheRejectingGate()
    {
        var detector = new ConflictDetector { Scope = ConflictScope.All };

        // Inside the envelope but vertically separated beyond caution → VerticalSeparation.
        var vertical = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 4.0 / 60, 0, 6000),
        }, T0);
        Assert.Equal(PairGate.VerticalSeparation, Assert.Single(vertical.NearPairs).Gate);

        // Inside the envelope but horizontally outside caution → HorizontalSeparation.
        var horizontal = detector.Evaluate(new[]
        {
            Ac("C", 53.0, 0, 4000),
            Ac("D", 53.0 + 7.0 / 60, 0, 4000),
        }, T0);
        Assert.Equal(PairGate.HorizontalSeparation, Assert.Single(horizontal.NearPairs).Gate);

        // First tick inside caution with no history → FirstSighting.
        var first = detector.Evaluate(new[]
        {
            Ac("E", 53.0, 0, 4000),
            Ac("F", 53.0 + 2.0 / 60, 0, 4000),
        }, T0);
        Assert.Equal(PairGate.FirstSighting, Assert.Single(first.NearPairs).Gate);

        // Flagged pairs are traced too, with their severity.
        var flagged = detector.Evaluate(new[]
        {
            Ac("E", 53.0, 0, 4000),
            Ac("F", 53.0 + 1.5 / 60, 0, 4000),
        }, T0.AddSeconds(10));
        var trace = Assert.Single(flagged.NearPairs);
        Assert.Equal(PairGate.Flagged, trace.Gate);
        Assert.Equal(ConflictSeverity.Conflict, trace.Severity);
    }

    [Fact]
    public void ThresholdsAreConfigurable()
    {
        var detector = new ConflictDetector(new ConflictThresholds(
            ConflictHorizontalNm: 1.0, ConflictVerticalFt: 500,
            CautionHorizontalNm: 2.0, CautionVerticalFt: 800)) { Scope = ConflictScope.All };

        detector.Evaluate(new[] { Ac("A", 53.0, 0, 4000), Ac("B", 53.0 + 1.9 / 60, 0, 4300) }, T0);
        var result = detector.Evaluate(new[]
        {
            Ac("A", 53.0, 0, 4000),
            Ac("B", 53.0 + 1.5 / 60, 0, 4300),
        }, T0.AddSeconds(10));

        // 1.5 nm / 300 ft: conflict-vertical is met but not conflict-horizontal → caution.
        Assert.Equal(ConflictSeverity.Caution, Assert.Single(result.Conflicts).Severity);
    }
}
