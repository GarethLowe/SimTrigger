using Serilog.Core;
using Serilog.Events;

namespace SimLauncher.App;

/// <summary>Serilog sink feeding the in-UI session log. Static so it exists before the host builds.</summary>
public sealed class UiLogSink : ILogEventSink
{
    public static UiLogSink Instance { get; } = new();

    public event Action<string>? LineEmitted;

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information)
        {
            return;
        }
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{Abbrev(logEvent.Level)}] {logEvent.RenderMessage()}";
        LineEmitted?.Invoke(line);
    }

    private static string Abbrev(LogEventLevel level) => level switch
    {
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => level.ToString().ToUpperInvariant(),
    };
}
