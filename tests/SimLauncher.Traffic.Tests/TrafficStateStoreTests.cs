using SimLauncher.Traffic;
using Xunit;

namespace SimLauncher.Traffic.Tests;

public class TrafficStateStoreTests
{
    private static TrafficAircraft Ac(string callsign, double alt = 1000, bool isPlayer = false)
        => new() { Callsign = callsign, Alt = alt, IsPlayer = isPlayer, InSim = true };

    private static AircraftUpdateMessage Update(params TrafficAircraft[] aircraft)
        => new() { Aircraft = aircraft };

    [Fact]
    public void UpsertsByCallsignAndRemovesStale()
    {
        var store = new TrafficStateStore();

        var first = store.ApplyUpdate(Update(Ac("A1"), Ac("B2")));
        Assert.Equal(2, first.Added.Count);
        Assert.Empty(first.Removed);

        // A1 updated, B2 gone stale, C3 new.
        var second = store.ApplyUpdate(Update(Ac("A1", alt: 2000), Ac("C3")));
        Assert.Equal(new[] { "C3" }, second.Added.Select(a => a.Callsign));
        Assert.Equal(new[] { "A1" }, second.Updated.Select(a => a.Callsign));
        Assert.Equal(new[] { "B2" }, second.Removed);

        Assert.Equal(2000, store.Get("A1")!.Alt);
        Assert.False(store.Contains("B2"));
        Assert.True(store.Contains("C3"));
    }

    [Fact]
    public void ApplyRemoveDropsAircraft()
    {
        var store = new TrafficStateStore();
        store.ApplyUpdate(Update(Ac("A1")));

        Assert.True(store.ApplyRemove("A1"));
        Assert.False(store.Contains("A1"));
        Assert.False(store.ApplyRemove("A1"));
    }

    [Fact]
    public void CallsignsAreCaseSensitive()
    {
        var store = new TrafficStateStore();
        store.ApplyUpdate(Update(Ac("Sht65w")));
        Assert.False(store.Contains("SHT65W"));
        Assert.True(store.Contains("Sht65w"));
    }

    [Fact]
    public void ExposesPlayer()
    {
        var store = new TrafficStateStore();
        store.ApplyUpdate(Update(Ac("AI1"), Ac("ME", isPlayer: true)));
        Assert.Equal("ME", store.Player!.Callsign);
    }

    [Fact]
    public void ClearEmptiesTheStore()
    {
        var store = new TrafficStateStore();
        store.ApplyUpdate(Update(Ac("A1")));
        store.Clear();
        Assert.Empty(store.Aircraft);
    }
}
