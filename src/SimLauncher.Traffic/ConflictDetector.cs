namespace SimLauncher.Traffic;

public enum ConflictSeverity
{
    Caution,
    Conflict,
}

/// <summary>Which aircraft pairs the detector examines.</summary>
public enum ConflictScope
{
    /// <summary>Only pairs where one side is the player. The default.</summary>
    PlayerVsAi,
    /// <summary>Every airborne pair.</summary>
    All,
    /// <summary>Only AI-vs-AI pairs; the player is never paired.</summary>
    AiVsAi,
}

/// <summary>Why an aircraft was left out of conflict detection this tick.</summary>
[Flags]
public enum ExclusionReasons
{
    None = 0,
    NotInSim = 1,
    OnGround = 2,
    PlayerExcludedByScope = 4,
}

/// <summary>The gate that decided a near-pair's fate this tick.</summary>
public enum PairGate
{
    /// <summary>Passed every gate; the pair is in <see cref="ConflictEvaluation.Conflicts"/>.</summary>
    Flagged,
    /// <summary>No range history yet — closure unknown, cannot flag.</summary>
    FirstSighting,
    /// <summary>Range steady or opening.</summary>
    NotClosing,
    /// <summary>Vertical separation at or above the caution threshold.</summary>
    VerticalSeparation,
    /// <summary>Horizontal separation at or above the caution threshold.</summary>
    HorizontalSeparation,
}

public sealed record ConflictThresholds(
    double ConflictHorizontalNm = 3.0,
    double ConflictVerticalFt = 1000,
    double CautionHorizontalNm = 5.0,
    double CautionVerticalFt = 1500);

public sealed record ConflictPair(
    TrafficAircraft A,
    TrafficAircraft B,
    double HorizontalNm,
    double VerticalFt,
    double ClosureKnots,
    ConflictSeverity Severity)
{
    public string PairKey => MakeKey(A.Callsign, B.Callsign);

    public static string MakeKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
}

public sealed record AircraftExclusion(TrafficAircraft Aircraft, ExclusionReasons Reasons);

/// <summary>Per-tick trace of a pair inside the diagnostic envelope, flagged or not.</summary>
public sealed record PairDiagnostic(
    TrafficAircraft A,
    TrafficAircraft B,
    double HorizontalNm,
    double VerticalFt,
    double ClosureKnots,
    bool HasHistory,
    PairGate Gate,
    ConflictSeverity? Severity)
{
    public string PairKey => ConflictPair.MakeKey(A.Callsign, B.Callsign);
}

/// <summary>Everything one tick of detection concluded, including why pairs were NOT flagged.</summary>
public sealed record ConflictEvaluation(
    IReadOnlyList<ConflictPair> Conflicts,
    IReadOnlyList<PairDiagnostic> NearPairs,
    IReadOnlyList<AircraftExclusion> Excluded,
    int EligibleCount);

/// <summary>
/// The check BeyondATC's sequencer doesn't do: true pairwise 3D proximity. A pair is
/// flagged only while the range is closing, so two aircraft holding steady separation
/// stay quiet. Stateful — it remembers last range per pair to compute closure.
/// Every pair inside the diagnostic envelope (caution thresholds ×
/// <see cref="DiagnosticEnvelopeFactor"/>) is traced in <see cref="ConflictEvaluation.NearPairs"/>
/// with the gate that stopped it, so a silent miss is always explainable from the log.
/// </summary>
public sealed class ConflictDetector
{
    /// <summary>
    /// Diagnostic envelope multiplier over the caution thresholds. Range history is kept
    /// for the whole envelope, so a pair entering the caution band already has closure
    /// data and can be flagged on its first tick inside the band.
    /// </summary>
    public const double DiagnosticEnvelopeFactor = 2.0;

    private readonly Dictionary<string, (double RangeNm, DateTimeOffset At)> _lastRange = new(StringComparer.Ordinal);

    public ConflictThresholds Thresholds { get; set; }

    public ConflictScope Scope { get; set; } = ConflictScope.PlayerVsAi;

    public ConflictDetector(ConflictThresholds? thresholds = null)
    {
        Thresholds = thresholds ?? new ConflictThresholds();
    }

    public ConflictEvaluation Evaluate(IReadOnlyCollection<TrafficAircraft> aircraft, DateTimeOffset now)
    {
        var t = Thresholds;
        var envelopeHNm = t.CautionHorizontalNm * DiagnosticEnvelopeFactor;
        var envelopeVFt = t.CautionVerticalFt * DiagnosticEnvelopeFactor;

        var eligible = new List<TrafficAircraft>();
        var excluded = new List<AircraftExclusion>();
        foreach (var a in aircraft)
        {
            var reasons = ExclusionReasons.None;
            // The player is exempt from the inSim requirement: BATC reports the user's own
            // aircraft, which is not injected traffic, so its inSim flag can be false.
            if (!a.InSim && !a.IsPlayer)
            {
                reasons |= ExclusionReasons.NotInSim;
            }
            // atDestination is deliberately NOT an exclusion: BATC sets it for arrivals
            // that are still airborne (short final, go-arounds), and a landed aircraft is
            // excluded by onGround anyway. Culling still refuses atDestination targets.
            if (a.OnGround)
            {
                reasons |= ExclusionReasons.OnGround;
            }
            if (Scope == ConflictScope.AiVsAi && a.IsPlayer)
            {
                reasons |= ExclusionReasons.PlayerExcludedByScope;
            }

            if (reasons == ExclusionReasons.None)
            {
                eligible.Add(a);
            }
            else
            {
                excluded.Add(new AircraftExclusion(a, reasons));
            }
        }

        var conflicts = new List<ConflictPair>();
        var nearPairs = new List<PairDiagnostic>();
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        // 1 degree of latitude is 60 nm; anything farther apart than the envelope radius
        // in latitude alone can't be a near pair, which keeps the O(n²) loop cheap.
        var latCutoffDeg = envelopeHNm / 60.0 * 1.1;

        for (var i = 0; i < eligible.Count; i++)
        {
            for (var j = i + 1; j < eligible.Count; j++)
            {
                var a = eligible[i];
                var b = eligible[j];

                if (Scope == ConflictScope.PlayerVsAi && !a.IsPlayer && !b.IsPlayer)
                {
                    continue;
                }
                if (Math.Abs(a.Lat - b.Lat) > latCutoffDeg)
                {
                    continue;
                }

                var verticalFt = Math.Abs(a.Alt - b.Alt);
                if (verticalFt >= envelopeVFt)
                {
                    continue;
                }

                var horizontalNm = GeoMath.HaversineNm(a.Lat, a.Lon, b.Lat, b.Lon);
                if (horizontalNm >= envelopeHNm)
                {
                    continue;
                }

                // Inside the envelope: track range history and trace the gate outcome.
                var key = ConflictPair.MakeKey(a.Callsign, b.Callsign);
                seenPairs.Add(key);

                var closureKnots = 0.0;
                var closing = false;
                var hasHistory = false;
                if (_lastRange.TryGetValue(key, out var last))
                {
                    var hours = (now - last.At).TotalHours;
                    if (hours > 0)
                    {
                        hasHistory = true;
                        closureKnots = (last.RangeNm - horizontalNm) / hours;
                        closing = closureKnots > 0;
                    }
                }
                _lastRange[key] = (horizontalNm, now);

                PairGate gate;
                ConflictSeverity? severity = null;
                if (verticalFt >= t.CautionVerticalFt)
                {
                    gate = PairGate.VerticalSeparation;
                }
                else if (horizontalNm >= t.CautionHorizontalNm)
                {
                    gate = PairGate.HorizontalSeparation;
                }
                else if (!hasHistory)
                {
                    gate = PairGate.FirstSighting;
                }
                else if (!closing)
                {
                    gate = PairGate.NotClosing;
                }
                else
                {
                    gate = PairGate.Flagged;
                    severity = horizontalNm < t.ConflictHorizontalNm && verticalFt < t.ConflictVerticalFt
                        ? ConflictSeverity.Conflict
                        : ConflictSeverity.Caution;
                    conflicts.Add(new ConflictPair(a, b, horizontalNm, verticalFt, closureKnots, severity.Value));
                }

                nearPairs.Add(new PairDiagnostic(a, b, horizontalNm, verticalFt, closureKnots, hasHistory, gate, severity));
            }
        }

        // Forget pairs that left the envelope (or vanished) so a re-approach starts fresh.
        foreach (var stale in _lastRange.Keys.Where(k => !seenPairs.Contains(k)).ToList())
        {
            _lastRange.Remove(stale);
        }

        return new ConflictEvaluation(conflicts, nearPairs, excluded, eligible.Count);
    }
}
