namespace SimLauncher.Traffic;

/// <summary>
/// Picks which aircraft of a conflicting pair to remove: never the player, never one on
/// the ground or already arrived, and between two valid candidates prefer the intruder —
/// the one behind and/or higher, i.e. not the aircraft about to land ahead.
/// </summary>
public static class CullPolicy
{
    /// <summary>An aircraft the other one is chasing counts as "in front" within this cone.</summary>
    private const double BehindConeDeg = 70;

    /// <summary>Altitude differences below this are treated as level for tie-breaking.</summary>
    private const double LevelBandFt = 200;

    public static TrafficAircraft? SelectTarget(ConflictPair pair)
    {
        var aOk = IsCullable(pair.A);
        var bOk = IsCullable(pair.B);
        if (!aOk && !bOk)
        {
            return null;
        }
        if (aOk != bOk)
        {
            return aOk ? pair.A : pair.B;
        }

        var scoreA = IntruderScore(pair.A, pair.B);
        var scoreB = IntruderScore(pair.B, pair.A);
        if (scoreA != scoreB)
        {
            return scoreA > scoreB ? pair.A : pair.B;
        }

        // Same score: prefer the higher one, then fall back to a stable ordering so
        // repeated evaluations of the same pair always name the same target.
        if (Math.Abs(pair.A.Alt - pair.B.Alt) > LevelBandFt)
        {
            return pair.A.Alt > pair.B.Alt ? pair.A : pair.B;
        }
        return string.CompareOrdinal(pair.A.Callsign, pair.B.Callsign) > 0 ? pair.A : pair.B;
    }

    public static bool IsCullable(TrafficAircraft a)
        => !a.IsPlayer && a.InSim && !a.OnGround && !a.AtDestination;

    private static int IntruderScore(TrafficAircraft candidate, TrafficAircraft other)
    {
        var score = 0;
        if (IsBehind(candidate, other))
        {
            score += 2;
        }
        if (candidate.Alt - other.Alt > LevelBandFt)
        {
            score += 1;
        }
        return score;
    }

    /// <summary>True when <paramref name="other"/> sits inside the candidate's forward cone.</summary>
    private static bool IsBehind(TrafficAircraft candidate, TrafficAircraft other)
    {
        var bearingToOther = GeoMath.InitialBearingDeg(candidate.Lat, candidate.Lon, other.Lat, other.Lon);
        return GeoMath.HeadingDifferenceDeg(candidate.Heading, bearingToOther) <= BehindConeDeg;
    }
}
