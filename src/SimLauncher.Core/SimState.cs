namespace SimLauncher.Core;

public enum SimStateEventKind
{
    /// <summary>A SimConnect connection was accepted by the sim.</summary>
    ConnectionOpened,
    /// <summary>The SimConnect connection dropped (may be transient).</summary>
    ConnectionLost,
    /// <summary>SimConnect "FlightLoaded" system event.</summary>
    FlightLoaded,
    /// <summary>CAMERA STATE simvar changed; Value carries the new state.</summary>
    CameraState,
    /// <summary>SimConnect "SimStart" system event (debug/informational).</summary>
    SimStart,
    /// <summary>SimConnect "Sim" running-state event; Value is 0/1 (debug/informational).</summary>
    SimRunning,
    /// <summary>SimConnect "Pause" event; Value is 0/1 (debug/informational).</summary>
    Pause,
    /// <summary>SimConnect "Quit" — the sim is shutting down.</summary>
    Quit,
}

public readonly record struct SimStateEvent(SimStateEventKind Kind, double Value = 0)
{
    public override string ToString() => Kind switch
    {
        SimStateEventKind.CameraState => $"CameraState={Value:0}",
        SimStateEventKind.SimRunning => $"Sim={(Value != 0 ? "running" : "stopped")}",
        SimStateEventKind.Pause => $"Pause={(Value != 0 ? "on" : "off")}",
        _ => Kind.ToString(),
    };
}

/// <summary>Live aircraft data for the debug panel; not used by the state machine.</summary>
public readonly record struct SimTelemetry(
    string AircraftTitle,
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double IndicatedAirspeedKnots);

/// <summary>
/// Source of raw sim lifecycle signals. The production implementation lives in
/// SimLauncher.SimConnect; tests drive a fake. Implementations must be resilient:
/// connection failures while the sim is not yet up are expected and never fatal.
/// </summary>
public interface ISimStateSource : IDisposable
{
    /// <summary>Raised for every raw sim signal. May fire on any thread.</summary>
    event EventHandler<SimStateEvent>? StateEvent;

    /// <summary>Raised periodically with aircraft/position data while connected (debug only).</summary>
    event EventHandler<SimTelemetry>? TelemetryReceived;

    /// <summary>True while a SimConnect connection is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Begin polling for a sim connection (every pollInterval until success).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop polling and drop any open connection without emitting Quit.</summary>
    Task StopAsync();
}
