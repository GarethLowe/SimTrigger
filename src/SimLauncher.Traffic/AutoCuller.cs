namespace SimLauncher.Traffic;

public sealed class AutoCullOptions
{
    public bool Enabled { get; set; }

    /// <summary>Log the would-be removal instead of sending it. Stays on until the user trusts the policy.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>A CONFLICT must persist this long before we act — act early, but not on a single blip.</summary>
    public double SustainSeconds { get; set; } = 2;

    /// <summary>Never re-target the same callsign within this window.</summary>
    public double CooldownSeconds { get; set; } = 120;
}

public sealed record CullDecision(string TargetCallsign, ConflictPair Pair, bool DryRun);

/// <summary>
/// Turns sustained CONFLICT pairs into removal decisions. Deliberately conservative:
/// disabled by default, dry-run by default, suppressed entirely after any protocol
/// anomaly, and debounced per pair and per callsign.
/// </summary>
public sealed class AutoCuller
{
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _conflictSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _actedOn = new(StringComparer.Ordinal);

    public AutoCullOptions Options { get; set; } = new();

    /// <summary>Set when the feed sends something unrecognised; cleared on reconnect.</summary>
    public bool Suppressed { get; set; }

    public AutoCuller(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public void Reset()
    {
        _conflictSince.Clear();
    }

    /// <param name="callsignExistsInFeed">
    /// Round-trip check: the target must still be present in the live feed at send time.
    /// </param>
    public IReadOnlyList<CullDecision> Evaluate(
        IReadOnlyList<ConflictPair> conflicts,
        Func<string, bool> callsignExistsInFeed)
    {
        var now = _time.GetUtcNow();
        var activeConflictKeys = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<CullDecision>();

        foreach (var pair in conflicts.Where(c => c.Severity == ConflictSeverity.Conflict))
        {
            activeConflictKeys.Add(pair.PairKey);

            if (!Options.Enabled || Suppressed)
            {
                continue;
            }

            if (!_conflictSince.TryGetValue(pair.PairKey, out var since))
            {
                _conflictSince[pair.PairKey] = now;
                continue;
            }
            if ((now - since).TotalSeconds < Options.SustainSeconds)
            {
                continue;
            }

            var target = CullPolicy.SelectTarget(pair);
            if (target is null || !callsignExistsInFeed(target.Callsign))
            {
                continue;
            }
            if (_actedOn.TryGetValue(target.Callsign, out var acted)
                && (now - acted).TotalSeconds < Options.CooldownSeconds)
            {
                continue;
            }

            _actedOn[target.Callsign] = now;
            _conflictSince.Remove(pair.PairKey);
            decisions.Add(new CullDecision(target.Callsign, pair, Options.DryRun));
        }

        // Pairs no longer in conflict must re-sustain from scratch next time.
        foreach (var stale in _conflictSince.Keys.Where(k => !activeConflictKeys.Contains(k)).ToList())
        {
            _conflictSince.Remove(stale);
        }
        foreach (var expired in _actedOn.Where(kv => (now - kv.Value).TotalSeconds >= Options.CooldownSeconds)
                     .Select(kv => kv.Key).ToList())
        {
            _actedOn.Remove(expired);
        }

        return decisions;
    }
}
