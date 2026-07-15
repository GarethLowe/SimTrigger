using System.Text.Json;

namespace SimLauncher.Traffic;

public abstract class TrafficMessage
{
}

/// <summary>Full-state push: upsert every entry by callsign, drop callsigns no longer present.</summary>
public sealed class AircraftUpdateMessage : TrafficMessage
{
    public required IReadOnlyList<TrafficAircraft> Aircraft { get; init; }
    public string? PlayerDepAirport { get; init; }
    public string? PlayerArrAirport { get; init; }
    public string? SimTime { get; init; }

    /// <summary>True if entries were dropped or looked wrong; auto-cull must not act on this data.</summary>
    public bool HadAnomalies { get; init; }
}

public sealed class RemoveAircraftMessage : TrafficMessage
{
    public required string Callsign { get; init; }
}

/// <summary>Anything that didn't parse as a known message. Carries the raw text for logging.</summary>
public sealed class UnknownMessage : TrafficMessage
{
    public string? Type { get; init; }
    public required string Raw { get; init; }
}

public static class TrafficMessageParser
{
    // Exact wire names via [JsonPropertyName]; case-insensitive matching stays OFF on purpose.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Never throws: anything malformed becomes an <see cref="UnknownMessage"/>.</summary>
    public static TrafficMessage Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                return new UnknownMessage { Raw = json };
            }

            var type = typeProp.GetString();
            return type switch
            {
                "aircraft-update" => ParseAircraftUpdate(doc.RootElement, json),
                // The spec says "remove-aircraft" but the live feed emits "aircraft-remove"
                // for its removal notices; accept both. Outbound commands stay "remove-aircraft".
                "remove-aircraft" or "aircraft-remove" => ParseRemoveAircraft(doc.RootElement, type, json),
                _ => new UnknownMessage { Type = type, Raw = json },
            };
        }
        catch (JsonException)
        {
            return new UnknownMessage { Raw = json };
        }
    }

    private static TrafficMessage ParseAircraftUpdate(JsonElement root, string json)
    {
        if (!root.TryGetProperty("aircraft", out var aircraftProp)
            || aircraftProp.ValueKind != JsonValueKind.Array)
        {
            return new UnknownMessage { Type = "aircraft-update", Raw = json };
        }

        var aircraft = new List<TrafficAircraft>();
        var anomalies = false;
        foreach (var element in aircraftProp.EnumerateArray())
        {
            TrafficAircraft? parsed;
            try
            {
                parsed = element.Deserialize<TrafficAircraft>(JsonOptions);
            }
            catch (JsonException)
            {
                anomalies = true;
                continue;
            }
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Callsign))
            {
                anomalies = true;
                continue;
            }
            Normalize(parsed);
            aircraft.Add(parsed);
        }

        return new AircraftUpdateMessage
        {
            Aircraft = aircraft,
            PlayerDepAirport = GetOptionalString(root, "playerDepAirport"),
            PlayerArrAirport = GetOptionalString(root, "playerArrAirport"),
            SimTime = GetOptionalString(root, "simTime"),
            HadAnomalies = anomalies,
        };
    }

    private static TrafficMessage ParseRemoveAircraft(JsonElement root, string type, string json)
    {
        var callsign = GetOptionalString(root, "callsign");
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return new UnknownMessage { Type = type, Raw = json };
        }
        return new RemoveAircraftMessage { Callsign = callsign };
    }

    private static string? GetOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>JSON null on a string field bypasses the property initializer; coalesce back to "".</summary>
    private static void Normalize(TrafficAircraft a)
    {
        a.Callsign = a.Callsign ?? "";
        a.Type = a.Type ?? "";
        a.State = a.State ?? "";
        a.IcaoFrom = a.IcaoFrom ?? "";
        a.IcaoTo = a.IcaoTo ?? "";
        a.TimeFrom = a.TimeFrom ?? "";
        a.TimeTo = a.TimeTo ?? "";
        a.Livery = a.Livery ?? "";
        a.TailNumber = a.TailNumber ?? "";
        a.Sid = a.Sid ?? "";
        a.Star = a.Star ?? "";
        a.DepRunway = a.DepRunway ?? "";
        a.ArrRunway = a.ArrRunway ?? "";
        a.Category = a.Category ?? "";
        a.Freq = a.Freq ?? "";
        a.FreqName = a.FreqName ?? "";
        a.FreqType = a.FreqType ?? "";
        a.Status = a.Status ?? "";
        a.Who = a.Who ?? "";
    }

    /// <summary>Builds the outbound removal command for an exact feed callsign.</summary>
    public static string BuildRemoveCommand(string callsign)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "remove-aircraft",
            ["callsign"] = callsign,
        });
}
