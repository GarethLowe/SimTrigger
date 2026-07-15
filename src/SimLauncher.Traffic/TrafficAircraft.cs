using System.Text.Json.Serialization;

namespace SimLauncher.Traffic;

/// <summary>
/// One aircraft as reported by BeyondATC's traffic WebSocket. Property names mirror
/// the wire format exactly (camelCase, case-sensitive) — do not rename.
/// </summary>
public sealed class TrafficAircraft
{
    [JsonPropertyName("callsign")] public string Callsign { get; set; } = "";
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("heading")] public double Heading { get; set; }
    [JsonPropertyName("alt")] public double Alt { get; set; }
    [JsonPropertyName("speed")] public double Speed { get; set; }
    [JsonPropertyName("groundspeed")] public double Groundspeed { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("icaoFrom")] public string IcaoFrom { get; set; } = "";
    [JsonPropertyName("icaoTo")] public string IcaoTo { get; set; } = "";
    [JsonPropertyName("timeFrom")] public string TimeFrom { get; set; } = "";
    [JsonPropertyName("timeTo")] public string TimeTo { get; set; } = "";
    [JsonPropertyName("livery")] public string Livery { get; set; } = "";
    [JsonPropertyName("tailNumber")] public string TailNumber { get; set; } = "";
    [JsonPropertyName("inSim")] public bool InSim { get; set; }
    [JsonPropertyName("onGround")] public bool OnGround { get; set; }
    [JsonPropertyName("isPlayer")] public bool IsPlayer { get; set; }
    [JsonPropertyName("atDestination")] public bool AtDestination { get; set; }
    [JsonPropertyName("sid")] public string Sid { get; set; } = "";
    [JsonPropertyName("star")] public string Star { get; set; } = "";
    [JsonPropertyName("depRunway")] public string DepRunway { get; set; } = "";
    [JsonPropertyName("arrRunway")] public string ArrRunway { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("lengthMeters")] public double LengthMeters { get; set; }
    [JsonPropertyName("freq")] public string Freq { get; set; } = "";
    [JsonPropertyName("freqName")] public string FreqName { get; set; } = "";
    [JsonPropertyName("freqType")] public string FreqType { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("statusDetails")] public string[]? StatusDetails { get; set; }
    [JsonPropertyName("statusSeverity")] public double? StatusSeverity { get; set; }
    [JsonPropertyName("who")] public string Who { get; set; } = "";
}
