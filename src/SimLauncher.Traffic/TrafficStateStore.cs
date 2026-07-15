namespace SimLauncher.Traffic;

public sealed record StoreDelta(
    IReadOnlyList<TrafficAircraft> Added,
    IReadOnlyList<TrafficAircraft> Updated,
    IReadOnlyList<string> Removed);

/// <summary>
/// Current picture of the traffic feed, keyed by callsign. Every aircraft-update is a
/// full snapshot: entries present are upserted, entries absent are dropped as stale.
/// Not thread-safe; callers serialize access (the WS client delivers one message at a time).
/// </summary>
public sealed class TrafficStateStore
{
    private readonly Dictionary<string, TrafficAircraft> _byCallsign = new(StringComparer.Ordinal);

    public IReadOnlyCollection<TrafficAircraft> Aircraft => _byCallsign.Values;

    public TrafficAircraft? Player => _byCallsign.Values.FirstOrDefault(a => a.IsPlayer);

    public bool Contains(string callsign) => _byCallsign.ContainsKey(callsign);

    public TrafficAircraft? Get(string callsign)
        => _byCallsign.TryGetValue(callsign, out var aircraft) ? aircraft : null;

    public StoreDelta ApplyUpdate(AircraftUpdateMessage message)
    {
        var added = new List<TrafficAircraft>();
        var updated = new List<TrafficAircraft>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var aircraft in message.Aircraft)
        {
            seen.Add(aircraft.Callsign);
            if (_byCallsign.ContainsKey(aircraft.Callsign))
            {
                updated.Add(aircraft);
            }
            else
            {
                added.Add(aircraft);
            }
            _byCallsign[aircraft.Callsign] = aircraft;
        }

        var removed = _byCallsign.Keys.Where(cs => !seen.Contains(cs)).ToList();
        foreach (var callsign in removed)
        {
            _byCallsign.Remove(callsign);
        }

        return new StoreDelta(added, updated, removed);
    }

    public bool ApplyRemove(string callsign) => _byCallsign.Remove(callsign);

    /// <summary>Called when the WS link drops; the feed will resend a snapshot on reconnect.</summary>
    public void Clear() => _byCallsign.Clear();
}
