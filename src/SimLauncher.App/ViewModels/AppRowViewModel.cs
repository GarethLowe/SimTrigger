using SimLauncher.Core.Engine;

namespace SimLauncher.App.ViewModels;

/// <summary>One event row: app name, delay chip, live status/PID/uptime/countdown.</summary>
public sealed class AppRowViewModel : ObservableObject
{
    private readonly ManagedApp _app;
    private string _statusText = "";
    private string _detailText = "";
    private string _countdownText = "";
    private string _rowState = "Idle";

    public AppRowViewModel(ManagedApp app, Action<AppRowViewModel> onEdit)
    {
        _app = app;
        EditCommand = new RelayCommand(_ => onEdit(this));
        Refresh(sessionActive: false);
    }

    public ManagedApp App => _app;
    public string Name => _app.Name;

    public string DelayText => _app.Config.DelaySeconds switch
    {
        0 => "",
        var s when s >= 60 && s % 60 == 0 => $"+{s / 60} min",
        var s => $"+{s} s",
    };

    public bool HasDelay => _app.Config.DelaySeconds > 0;

    public string WaitText => string.IsNullOrEmpty(_app.Config.WaitForApp)
        ? ""
        : $"after {_app.Config.WaitForApp}";

    public bool HasWait => !string.IsNullOrEmpty(_app.Config.WaitForApp);

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public string CountdownText { get => _countdownText; private set => Set(ref _countdownText, value); }

    /// <summary>Drives row highlighting: Idle | Dimmed | Armed | Counting | Fired | Failed.</summary>
    public string RowState { get => _rowState; private set => Set(ref _rowState, value); }

    public RelayCommand EditCommand { get; }

    /// <summary>Recomputes display fields from the ManagedApp. Call on the UI thread.</summary>
    public void Refresh(bool sessionActive)
    {
        var status = _app.Status;
        StatusText = status switch
        {
            AppStatus.Idle => "",
            AppStatus.Waiting => "Waiting",
            AppStatus.Delayed => "Delayed",
            AppStatus.WaitingForDependency => $"Waiting for {_app.Config.WaitForApp}",
            AppStatus.Starting => "Starting",
            AppStatus.Running => _app.IsAdopted ? "Running (adopted)" : "Running",
            AppStatus.Exited => "Exited",
            AppStatus.Failed => "Failed",
            AppStatus.Skipped => "Skipped (already running)",
            _ => status.ToString(),
        };

        DetailText = status switch
        {
            AppStatus.Running when _app.Pid is int pid && _app.StartedAt is DateTimeOffset started =>
                $"PID {pid} · up {FormatUptime(DateTimeOffset.UtcNow - started.ToUniversalTime())}",
            AppStatus.Running when _app.StartedAt is DateTimeOffset started =>
                $"unmanaged · up {FormatUptime(DateTimeOffset.UtcNow - started.ToUniversalTime())}",
            AppStatus.Skipped when _app.Pid is int pid => $"PID {pid}",
            AppStatus.Failed => _app.LastError ?? "",
            _ => "",
        };

        CountdownText = status == AppStatus.Delayed && _app.CountdownEndsAt is DateTimeOffset ends
            ? FormatCountdown(ends.ToUniversalTime() - DateTimeOffset.UtcNow)
            : "";

        RowState = status switch
        {
            AppStatus.Failed => "Failed",
            AppStatus.Delayed or AppStatus.WaitingForDependency or AppStatus.Starting => "Counting",
            AppStatus.Running => "Fired",
            AppStatus.Exited or AppStatus.Skipped => "Done",
            AppStatus.Waiting => "Armed",
            _ => sessionActive ? "Dimmed" : "Idle",
        };
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FormatCountdown(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{t.Seconds}s";
    }
}
