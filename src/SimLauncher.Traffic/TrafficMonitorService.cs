using Microsoft.Extensions.Logging;

namespace SimLauncher.Traffic;

public sealed record TrafficSnapshot(
    IReadOnlyList<TrafficAircraft> Aircraft,
    IReadOnlyList<ConflictPair> Conflicts,
    string? SimTime,
    string? PlayerDepAirport,
    string? PlayerArrAirport);

public enum TrafficActionKind
{
    RemoveSent,
    RemoveDryRun,
    RemoveFailed,
    ProtocolAnomaly,
    Link,
    Detection,
}

/// <summary><paramref name="Detail"/> carries diagnostic payload (e.g. the raw feed message).</summary>
public sealed record TrafficActionLogEntry(DateTimeOffset At, TrafficActionKind Kind, string Text, string? Detail = null);

/// <summary>
/// Glues transport, state, conflict detection and the cull policy together. Owns no UI:
/// the panel subscribes to <see cref="SnapshotUpdated"/> / <see cref="ActionLogged"/> and
/// calls <see cref="RequestRemovalAsync"/> for the manual path. Runs independently of
/// process management — if BeyondATC isn't up, it just keeps retrying.
///
/// Detection diagnostics go to the "SimLauncher.Traffic.Detection" logger: per-tick
/// summaries and near-pair gate traces at Debug (structured file only), lifecycle and
/// player-eligibility events at Information+ (also mirrored to the panel's action log).
/// Every event carries the feed's simTime so it can be cross-correlated with
/// BeyondATC's own logs.
/// </summary>
public sealed class TrafficMonitorService : IAsyncDisposable, IDisposable
{
    public const string DetectionLoggerName = "SimLauncher.Traffic.Detection";

    private readonly ILogger _log;
    private readonly ILogger _detectionLog;
    private readonly TimeProvider _time;
    private readonly TrafficWebSocketClient _client;
    private readonly TrafficStateStore _store = new();
    private readonly ConflictDetector _detector = new();
    private readonly AutoCuller _culler;
    private readonly object _gate = new();

    // Edge-trigger state for the diagnostic log; all reset on reconnect.
    private readonly Dictionary<string, ExclusionReasons> _lastExclusions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ConflictSeverity Severity, DateTimeOffset Since)> _activePairs = new(StringComparer.Ordinal);
    private bool _playerMissingLogged;
    private ExclusionReasons _playerExclusionLogged;

    /// <summary>All events are raised on background threads; UI subscribers must marshal.</summary>
    public event Action<TrafficSnapshot>? SnapshotUpdated;
    public event Action<bool>? ConnectionChanged;
    public event Action<TrafficActionLogEntry>? ActionLogged;

    public bool IsConnected => _client.IsConnected;

    public TrafficMonitorService(ILoggerFactory loggers, Uri? uri = null, TimeProvider? time = null)
    {
        _log = loggers.CreateLogger<TrafficMonitorService>();
        _detectionLog = loggers.CreateLogger(DetectionLoggerName);
        _time = time ?? TimeProvider.System;
        _culler = new AutoCuller(_time);
        _client = new TrafficWebSocketClient(_log, uri);
        _client.MessageReceived += OnMessage;
        _client.ConnectionChanged += OnConnectionChanged;
    }

    public void Start() => _client.Start();

    public void UpdateSettings(ConflictThresholds thresholds, AutoCullOptions cullOptions,
        ConflictScope scope = ConflictScope.PlayerVsAi)
    {
        lock (_gate)
        {
            _detector.Thresholds = thresholds;
            _detector.Scope = scope;
            _culler.Options = cullOptions;
        }
    }

    /// <summary>
    /// Manual, user-confirmed removal. Refuses the player and callsigns not currently in
    /// the feed, so a stale click can never despawn the wrong aircraft.
    /// </summary>
    public async Task<bool> RequestRemovalAsync(string callsign, string reason)
    {
        lock (_gate)
        {
            var aircraft = _store.Get(callsign);
            if (aircraft is null)
            {
                Log(TrafficActionKind.RemoveFailed, $"Refused remove {callsign}: not in the current feed");
                return false;
            }
            if (aircraft.IsPlayer)
            {
                Log(TrafficActionKind.RemoveFailed, $"Refused remove {callsign}: player aircraft");
                return false;
            }
        }
        return await SendRemoveAsync(callsign, reason);
    }

    private async Task<bool> SendRemoveAsync(string callsign, string reason)
    {
        var ok = await _client.SendAsync(TrafficMessageParser.BuildRemoveCommand(callsign));
        if (ok)
        {
            Log(TrafficActionKind.RemoveSent, $"Sent remove {callsign} ({reason})");
        }
        else
        {
            Log(TrafficActionKind.RemoveFailed, $"Send failed for remove {callsign}: link down");
        }
        return ok;
    }

    private void OnConnectionChanged(bool connected)
    {
        lock (_gate)
        {
            _store.Clear();
            _lastExclusions.Clear();
            _activePairs.Clear();
            _playerMissingLogged = false;
            _playerExclusionLogged = ExclusionReasons.None;
            if (connected)
            {
                // Fresh session: forget the stale picture and lift any anomaly suppression.
                _culler.Suppressed = false;
                _culler.Reset();
            }
            else
            {
                PublishSnapshotLocked(null, null, null);
            }
        }
        Log(TrafficActionKind.Link, connected ? "Traffic link connected" : "Traffic link lost");
        ConnectionChanged?.Invoke(connected);
    }

    private void OnMessage(string json)
    {
        List<CullDecision> decisions;
        List<TrafficActionLogEntry> uiEntries;
        lock (_gate)
        {
            switch (TrafficMessageParser.Parse(json))
            {
                case AircraftUpdateMessage update:
                    if (update.HadAnomalies)
                    {
                        FlagAnomaly(json);
                    }
                    _store.ApplyUpdate(update);
                    var now = _time.GetUtcNow();
                    var eval = _detector.Evaluate(_store.Aircraft, now);
                    uiEntries = LogDetectionLocked(update, eval, now);
                    decisions = _culler.Evaluate(eval.Conflicts, _store.Contains).ToList();
                    PublishSnapshotLocked(update.SimTime, update.PlayerDepAirport, update.PlayerArrAirport, eval.Conflicts);
                    break;

                case RemoveAircraftMessage removal:
                    _store.ApplyRemove(removal.Callsign);
                    _detectionLog.LogDebug("Feed removed {Callsign}", removal.Callsign);
                    PublishSnapshotLocked(null, null, null);
                    return;

                case UnknownMessage unknown:
                    FlagAnomaly(unknown.Raw);
                    return;

                default:
                    return;
            }
        }

        foreach (var entry in uiEntries)
        {
            ActionLogged?.Invoke(entry);
        }

        foreach (var decision in decisions)
        {
            var pair = decision.Pair;
            var detail = $"{decision.TargetCallsign} (pair {pair.A.Callsign}/{pair.B.Callsign}: "
                         + $"{pair.HorizontalNm:0.0} nm / {pair.VerticalFt:0} ft, closing {pair.ClosureKnots:0} kt)";
            if (decision.DryRun)
            {
                Log(TrafficActionKind.RemoveDryRun, $"WOULD remove {detail}");
            }
            else
            {
                _ = SendRemoveAsync(decision.TargetCallsign, $"auto-cull: {detail}");
            }
        }
    }

    // ----- detection diagnostics -----

    /// <summary>
    /// File logging happens inline (structured, high volume, Debug); panel entries are
    /// collected and raised outside the lock. Everything user-facing is edge-triggered so
    /// the action log shows state changes, not a firehose.
    /// </summary>
    private List<TrafficActionLogEntry> LogDetectionLocked(
        AircraftUpdateMessage update, ConflictEvaluation eval, DateTimeOffset now)
    {
        var ui = new List<TrafficActionLogEntry>();
        var simTime = update.SimTime;
        var player = update.Aircraft.FirstOrDefault(a => a.IsPlayer);

        _detectionLog.LogDebug(
            "Tick {SimTime}: {AircraftCount} aircraft, {EligibleCount} eligible, {NearPairCount} near, {ConflictCount} flagged, scope {Scope}; "
            + "player {PlayerCallsign}: alt={PlayerAlt} gs={PlayerGroundspeed} inSim={PlayerInSim} gnd={PlayerOnGround} dest={PlayerAtDestination} state={PlayerState}",
            simTime, update.Aircraft.Count, eval.EligibleCount, eval.NearPairs.Count, eval.Conflicts.Count, _detector.Scope,
            player?.Callsign ?? "(none)", player?.Alt, player?.Groundspeed,
            player?.InSim, player?.OnGround, player?.AtDestination, player?.State);

        foreach (var p in eval.NearPairs)
        {
            _detectionLog.LogDebug(
                "NearPair {PairKey} at {SimTime}: gate={Gate} sev={Severity} h={HorizontalNm:0.00}nm v={VerticalFt:0}ft closure={ClosureKnots:0.0}kt hist={HasHistory} | "
                + "{ACallsign}: player={AIsPlayer} alt={AAlt:0} state={AState} status={AStatus} | "
                + "{BCallsign}: player={BIsPlayer} alt={BAlt:0} state={BState} status={BStatus}",
                p.PairKey, simTime, p.Gate, p.Severity, p.HorizontalNm, p.VerticalFt, p.ClosureKnots, p.HasHistory,
                p.A.Callsign, p.A.IsPlayer, p.A.Alt, p.A.State, p.A.Status,
                p.B.Callsign, p.B.IsPlayer, p.B.Alt, p.B.State, p.B.Status);
        }

        LogEligibilityTransitionsLocked(update, eval, simTime);
        LogPlayerEligibilityLocked(player, eval, simTime, now, ui);
        LogConflictLifecycleLocked(eval, simTime, now, ui);
        return ui;
    }

    private void LogEligibilityTransitionsLocked(
        AircraftUpdateMessage update, ConflictEvaluation eval, string? simTime)
    {
        var current = eval.Excluded.ToDictionary(e => e.Aircraft.Callsign, e => e, StringComparer.Ordinal);

        foreach (var aircraft in update.Aircraft)
        {
            var reasons = current.TryGetValue(aircraft.Callsign, out var exclusion)
                ? exclusion.Reasons
                : ExclusionReasons.None;
            _lastExclusions.TryGetValue(aircraft.Callsign, out var previous); // absent → None
            _lastExclusions[aircraft.Callsign] = reasons;
            if (reasons == previous)
            {
                continue; // covers new eligible aircraft too — the tick summary counts them
            }

            var level = aircraft.IsPlayer ? LogLevel.Information : LogLevel.Debug;
            _detectionLog.Log(level,
                "Eligibility {Callsign} at {SimTime}: {Previous} -> {Current} "
                + "(player={IsPlayer} inSim={InSim} gnd={OnGround} dest={AtDestination} alt={Alt:0} state={State} status={Status})",
                aircraft.Callsign, simTime, previous, reasons,
                aircraft.IsPlayer, aircraft.InSim, aircraft.OnGround, aircraft.AtDestination,
                aircraft.Alt, aircraft.State, aircraft.Status);
        }

        foreach (var gone in _lastExclusions.Keys.Where(cs => !_store.Contains(cs)).ToList())
        {
            _lastExclusions.Remove(gone);
        }
    }

    private void LogPlayerEligibilityLocked(TrafficAircraft? player, ConflictEvaluation eval,
        string? simTime, DateTimeOffset now, List<TrafficActionLogEntry> ui)
    {
        if (_detector.Scope != ConflictScope.PlayerVsAi)
        {
            return;
        }

        if (player is null)
        {
            if (!_playerMissingLogged)
            {
                _playerMissingLogged = true;
                _detectionLog.LogWarning(
                    "No isPlayer aircraft in feed at {SimTime} — Player-vs-AI detection has no pairs to check", simTime);
                ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                    "No player aircraft in feed — Player-vs-AI detection is idle"));
            }
            return;
        }
        if (_playerMissingLogged)
        {
            _playerMissingLogged = false;
            _detectionLog.LogInformation("Player {Callsign} back in feed at {SimTime}", player.Callsign, simTime);
            ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                $"Player {player.Callsign} back in feed — detection active"));
        }

        var exclusion = eval.Excluded.FirstOrDefault(e => e.Aircraft.IsPlayer)?.Reasons ?? ExclusionReasons.None;
        if (exclusion == _playerExclusionLogged)
        {
            return;
        }
        _playerExclusionLogged = exclusion;
        if (exclusion != ExclusionReasons.None)
        {
            _detectionLog.LogWarning(
                "Player {Callsign} excluded from detection at {SimTime}: {Reasons} — no conflicts can be flagged while this holds",
                player.Callsign, simTime, exclusion);
            ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                $"Player {player.Callsign} excluded from detection: {exclusion}"));
        }
        else
        {
            _detectionLog.LogInformation("Player {Callsign} eligible again at {SimTime}", player.Callsign, simTime);
            ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                $"Player {player.Callsign} eligible for detection again"));
        }
    }

    private void LogConflictLifecycleLocked(ConflictEvaluation eval, string? simTime,
        DateTimeOffset now, List<TrafficActionLogEntry> ui)
    {
        var flagged = eval.Conflicts.ToDictionary(c => c.PairKey, StringComparer.Ordinal);

        foreach (var (key, pair) in flagged)
        {
            var name = pair.Severity == ConflictSeverity.Conflict ? "CONFLICT" : "CAUTION";
            var detail = $"{pair.HorizontalNm:0.0} nm / {pair.VerticalFt:0} ft, closing {pair.ClosureKnots:0} kt";
            if (!_activePairs.TryGetValue(key, out var active))
            {
                _activePairs[key] = (pair.Severity, now);
                _detectionLog.LogInformation(
                    "{Severity} begin {PairKey} at {SimTime}: {HorizontalNm:0.00} nm / {VerticalFt:0} ft, closing {ClosureKnots:0.0} kt (playerInvolved={PlayerInvolved})",
                    name, key, simTime, pair.HorizontalNm, pair.VerticalFt, pair.ClosureKnots,
                    pair.A.IsPlayer || pair.B.IsPlayer);
                ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                    $"{name} begin {pair.A.Callsign} ↔ {pair.B.Callsign}", detail));
            }
            else if (active.Severity != pair.Severity)
            {
                _activePairs[key] = (pair.Severity, active.Since);
                var change = pair.Severity == ConflictSeverity.Conflict ? "escalated to CONFLICT" : "downgraded to CAUTION";
                _detectionLog.LogInformation("{PairKey} {Change} at {SimTime}: {Detail}", key, change, simTime, detail);
                ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                    $"{pair.A.Callsign} ↔ {pair.B.Callsign} {change}", detail));
            }
        }

        foreach (var (key, active) in _activePairs.Where(kv => !flagged.ContainsKey(kv.Key)).ToList())
        {
            _activePairs.Remove(key);
            var trace = eval.NearPairs.FirstOrDefault(p => p.PairKey == key);
            var why = trace is null ? "left envelope or became ineligible" : $"now {trace.Gate}";
            var seconds = (now - active.Since).TotalSeconds;
            var name = active.Severity == ConflictSeverity.Conflict ? "CONFLICT" : "CAUTION";
            _detectionLog.LogInformation(
                "{Severity} end {PairKey} at {SimTime} after {DurationSeconds:0} s ({Why})",
                name, key, simTime, seconds, why);
            ui.Add(new TrafficActionLogEntry(now, TrafficActionKind.Detection,
                $"{name} end {key.Replace("|", " ↔ ")} after {seconds:0} s ({why})"));
        }
    }

    // ----- plumbing -----

    private void FlagAnomaly(string raw)
    {
        // Fail safe: unrecognised data disarms auto-cull until the next reconnect.
        _culler.Suppressed = true;
        _log.LogWarning("Unrecognised traffic message; auto-cull suppressed. Raw: {Raw}", raw);
        Log(TrafficActionKind.ProtocolAnomaly,
            "Unrecognised feed data — auto-cull disarmed until reconnect",
            raw.Length > 4000 ? raw[..4000] + "… [truncated — full payload in the file log]" : raw);
    }

    private void PublishSnapshotLocked(string? simTime, string? dep, string? arr,
        IReadOnlyList<ConflictPair>? conflicts = null)
    {
        var snapshot = new TrafficSnapshot(
            _store.Aircraft.ToList(),
            conflicts ?? Array.Empty<ConflictPair>(),
            simTime, dep, arr);
        SnapshotUpdated?.Invoke(snapshot);
    }

    private void Log(TrafficActionKind kind, string text, string? detail = null)
    {
        _log.LogInformation("Traffic: {Text}", text);
        ActionLogged?.Invoke(new TrafficActionLogEntry(_time.GetUtcNow(), kind, text, detail));
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    public void Dispose() => _client.Dispose();
}
