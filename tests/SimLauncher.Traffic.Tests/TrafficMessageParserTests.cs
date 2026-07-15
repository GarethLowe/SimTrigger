using SimLauncher.Traffic;
using Xunit;

namespace SimLauncher.Traffic.Tests;

public class TrafficMessageParserTests
{
    private const string FullUpdate = """
    {
      "type": "aircraft-update",
      "aircraft": [
        {
          "callsign": "SHT65W",
          "lat": 51.885, "lon": 0.235, "heading": 227.4, "alt": 4100,
          "speed": 210, "groundspeed": 224, "type": "A320", "state": "Approach",
          "icaoFrom": "EGSS", "icaoTo": "EIDW", "timeFrom": "1030Z", "timeTo": "1145Z",
          "livery": "British Airways", "tailNumber": "G-EUYX",
          "inSim": true, "onGround": false, "isPlayer": false, "atDestination": false,
          "sid": "UTAVA1R", "star": "LAPMO1A", "depRunway": "22", "arrRunway": "28L",
          "category": "M", "lengthMeters": 37.6,
          "freq": "121.800", "freqName": "Dublin Tower", "freqType": "TWR",
          "status": "Cleared to land", "statusDetails": ["ILS 28L"], "statusSeverity": 1,
          "who": "Approach"
        }
      ],
      "playerDepAirport": "EGSS",
      "playerArrAirport": "EIDW",
      "simTime": "1124Z"
    }
    """;

    [Fact]
    public void ParsesFullAircraftUpdate()
    {
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(FullUpdate));

        Assert.False(msg.HadAnomalies);
        Assert.Equal("EGSS", msg.PlayerDepAirport);
        Assert.Equal("EIDW", msg.PlayerArrAirport);
        Assert.Equal("1124Z", msg.SimTime);

        var ac = Assert.Single(msg.Aircraft);
        Assert.Equal("SHT65W", ac.Callsign);
        Assert.Equal(51.885, ac.Lat);
        Assert.Equal(0.235, ac.Lon);
        Assert.Equal(227.4, ac.Heading);
        Assert.Equal(4100, ac.Alt);
        Assert.Equal(224, ac.Groundspeed);
        Assert.Equal("A320", ac.Type);
        Assert.Equal("Approach", ac.State);
        Assert.True(ac.InSim);
        Assert.False(ac.OnGround);
        Assert.False(ac.IsPlayer);
        Assert.Equal("28L", ac.ArrRunway);
        Assert.Equal(new[] { "ILS 28L" }, ac.StatusDetails);
        Assert.Equal(1.0, ac.StatusSeverity);
        Assert.Equal("Approach", ac.Who);
    }

    [Fact]
    public void MissingOptionalFieldsAreTolerated()
    {
        // statusDetails/statusSeverity absent, statusSeverity null elsewhere in the wild.
        var json = """
        {
          "type": "aircraft-update",
          "aircraft": [
            { "callsign": "RYR34", "lat": 53.4, "lon": -6.2, "heading": 90, "alt": 3000,
              "groundspeed": 180, "inSim": true, "onGround": false, "isPlayer": false,
              "statusSeverity": null }
          ],
          "simTime": "0900Z"
        }
        """;
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(json));
        Assert.False(msg.HadAnomalies);
        var ac = Assert.Single(msg.Aircraft);
        Assert.Null(ac.StatusSeverity);
        Assert.Null(ac.StatusDetails);
        Assert.Equal("", ac.Type);
        Assert.Null(msg.PlayerDepAirport);
    }

    [Fact]
    public void NullStringFieldsCoalesceToEmpty()
    {
        var json = """
        {
          "type": "aircraft-update",
          "aircraft": [
            { "callsign": "EIN12", "lat": 1, "lon": 2, "heading": 3, "alt": 4,
              "state": null, "icaoTo": null, "arrRunway": null }
          ]
        }
        """;
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(json));
        var ac = Assert.Single(msg.Aircraft);
        Assert.Equal("", ac.State);
        Assert.Equal("", ac.IcaoTo);
        Assert.Equal("", ac.ArrRunway);
    }

    [Fact]
    public void AircraftWithoutCallsignIsDroppedAndFlagged()
    {
        var json = """
        {
          "type": "aircraft-update",
          "aircraft": [
            { "lat": 1, "lon": 2 },
            { "callsign": "OK1", "lat": 1, "lon": 2, "heading": 0, "alt": 100 }
          ]
        }
        """;
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(json));
        Assert.True(msg.HadAnomalies);
        Assert.Equal("OK1", Assert.Single(msg.Aircraft).Callsign);
    }

    [Fact]
    public void WrongFieldTypeDropsEntryAndFlags()
    {
        var json = """
        {
          "type": "aircraft-update",
          "aircraft": [ { "callsign": "BAD1", "lat": "not-a-number" } ]
        }
        """;
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(json));
        Assert.True(msg.HadAnomalies);
        Assert.Empty(msg.Aircraft);
    }

    [Theory]
    [InlineData("""{ "type": "remove-aircraft", "callsign": "SHT65W" }""", "SHT65W")]
    // The live feed's removal notice uses this type name (observed 2026-07); both are accepted.
    [InlineData("""{"type":"aircraft-remove","callsign":"LFA204"}""", "LFA204")]
    public void ParsesRemovalNotices(string json, string expected)
    {
        var msg = Assert.IsType<RemoveAircraftMessage>(TrafficMessageParser.Parse(json));
        Assert.Equal(expected, msg.Callsign);
    }

    [Theory]
    [InlineData("""{ "type": "something-new", "payload": 1 }""")]
    [InlineData("""{ "no-type": true }""")]
    [InlineData("not json at all")]
    [InlineData("""{ "type": "remove-aircraft" }""")]
    [InlineData("""{ "type": "aircraft-update", "aircraft": "nope" }""")]
    public void UnrecognisedInputBecomesUnknownMessage(string json)
    {
        var msg = Assert.IsType<UnknownMessage>(TrafficMessageParser.Parse(json));
        Assert.Equal(json, msg.Raw);
    }

    [Fact]
    public void BuildRemoveCommandUsesExactCallsign()
    {
        Assert.Equal(
            """{"type":"remove-aircraft","callsign":"SHT65W"}""",
            TrafficMessageParser.BuildRemoveCommand("SHT65W"));
    }

    [Fact]
    public void CallsignMatchingIsCaseSensitive()
    {
        // The feed key must round-trip verbatim; "callSign" (wrong case) must not match.
        var json = """
        {
          "type": "aircraft-update",
          "aircraft": [ { "callSign": "WRONG", "lat": 1, "lon": 2 } ]
        }
        """;
        var msg = Assert.IsType<AircraftUpdateMessage>(TrafficMessageParser.Parse(json));
        Assert.True(msg.HadAnomalies);
        Assert.Empty(msg.Aircraft);
    }
}
