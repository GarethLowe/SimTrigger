using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using SimLauncher.Core.Config;
using SimLauncher.Traffic;

namespace SimLauncher.App.ViewModels;

public sealed class ConflictRowViewModel
{
    public required string PairText { get; init; }
    public required string DetailText { get; init; }
    public required string SeverityText { get; init; }
    public required bool IsConflict { get; init; }
    public required bool InvolvesPlayer { get; init; }
    public string? TargetCallsign { get; init; }
    public required RelayCommand RemoveCommand { get; init; }
    public bool CanRemove => TargetCallsign is not null;
    public string RemoveText => TargetCallsign is null ? "" : $"Remove {TargetCallsign}…";
}

public sealed class ActionLogRowViewModel
{
    public required string TimeText { get; init; }
    public required string KindText { get; init; }
    public required string KindForeground { get; init; }
    public required string KindBackground { get; init; }
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public required string ClipboardText { get; init; }
    public required RelayCommand CopyCommand { get; init; }

    public static ActionLogRowViewModel From(TrafficActionLogEntry entry)
    {
        var (kindText, fg, bg) = entry.Kind switch
        {
            TrafficActionKind.RemoveSent => ("SENT", "#7BC97F", "#24392A"),
            TrafficActionKind.RemoveDryRun => ("DRY", "#E5C07B", "#3A3A22"),
            TrafficActionKind.RemoveFailed => ("FAIL", "#F08080", "#4A2626"),
            TrafficActionKind.ProtocolAnomaly => ("FEED", "#F0B080", "#43302A"),
            TrafficActionKind.Link => ("LINK", "#4FC3F7", "#2C3A4A"),
            TrafficActionKind.Detection => ("DET", "#C792EA", "#332B3E"),
            _ => ("INFO", "#909090", "#2B2B2C"),
        };
        var clipboard = entry.Detail is null
            ? $"{entry.At.ToLocalTime():HH:mm:ss} [{kindText}] {entry.Text}"
            : $"{entry.At.ToLocalTime():HH:mm:ss} [{kindText}] {entry.Text}\n{entry.Detail}";
        return new ActionLogRowViewModel
        {
            TimeText = entry.At.ToLocalTime().ToString("HH:mm:ss"),
            KindText = kindText,
            KindForeground = fg,
            KindBackground = bg,
            Text = entry.Text,
            Detail = entry.Detail,
            ClipboardText = clipboard,
            CopyCommand = new RelayCommand(_ => System.Windows.Clipboard.SetText(clipboard)),
        };
    }
}

public sealed class TrafficViewModel : ObservableObject
{
    private readonly TrafficMonitorService _service;
    private readonly ConfigStore _config;
    private readonly Dispatcher _dispatcher;

    private bool _mapReady;
    private TrafficSnapshot? _latest;
    private bool _linkConnected;
    private string _linkStatusText = "Traffic link: disconnected";
    private string _simTimeText = "—";
    private string _routeText = "—";
    private string _countText = "0 aircraft";
    private string _thresholdStatus = "";
    private bool _trailsEnabled;
    private bool _labelsEnabled;
    private string _conflictHNm;
    private string _conflictVFt;
    private string _cautionHNm;
    private string _cautionVFt;

    /// <summary>Raised on the UI thread with a JSON message for the map page.</summary>
    public event Action<string>? PostToMap;

    /// <summary>Set by the window: shows a confirm dialog. Args: callsign, detail line.</summary>
    public Func<string, string, bool>? ConfirmRemoval { get; set; }

    public TrafficViewModel(TrafficMonitorService service, ConfigStore config)
    {
        _service = service;
        _config = config;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var t = config.Current.Traffic;
        _conflictHNm = t.ConflictHorizontalNm.ToString(CultureInfo.InvariantCulture);
        _conflictVFt = t.ConflictVerticalFt.ToString(CultureInfo.InvariantCulture);
        _cautionHNm = t.CautionHorizontalNm.ToString(CultureInfo.InvariantCulture);
        _cautionVFt = t.CautionVerticalFt.ToString(CultureInfo.InvariantCulture);

        ApplyThresholdsCommand = new RelayCommand(_ => ApplyThresholds());
        RecenterCommand = new RelayCommand(_ => PostToMap?.Invoke("""{"type":"recenter"}"""));

        _service.SnapshotUpdated += snapshot => Post(() => OnSnapshot(snapshot));
        _service.ConnectionChanged += connected => Post(() => OnConnection(connected));
        _service.ActionLogged += entry => Post(() => OnAction(entry));
        OnConnection(_service.IsConnected);
    }

    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = new();
    public ObservableCollection<ActionLogRowViewModel> ActionLog { get; } = new();

    public RelayCommand ApplyThresholdsCommand { get; }
    public RelayCommand RecenterCommand { get; }

    public bool LinkConnected { get => _linkConnected; private set => Set(ref _linkConnected, value); }
    public string LinkStatusText { get => _linkStatusText; private set => Set(ref _linkStatusText, value); }
    public string SimTimeText { get => _simTimeText; private set => Set(ref _simTimeText, value); }
    public string RouteText { get => _routeText; private set => Set(ref _routeText, value); }
    public string CountText { get => _countText; private set => Set(ref _countText, value); }
    public string ThresholdStatus { get => _thresholdStatus; private set => Set(ref _thresholdStatus, value); }

    public string ConflictHNm { get => _conflictHNm; set => Set(ref _conflictHNm, value); }
    public string ConflictVFt { get => _conflictVFt; set => Set(ref _conflictVFt, value); }
    public string CautionHNm { get => _cautionHNm; set => Set(ref _cautionHNm, value); }
    public string CautionVFt { get => _cautionVFt; set => Set(ref _cautionVFt, value); }

    private static readonly string[] ScopeOptions = { "Player vs AI only", "All pairs", "AI vs AI only" };

    public IReadOnlyList<string> ConflictScopeOptions => ScopeOptions;

    public string SelectedConflictScope
    {
        get => _config.Current.Traffic.ConflictScope switch
        {
            ConflictScopeSetting.All => ScopeOptions[1],
            ConflictScopeSetting.AiVsAi => ScopeOptions[2],
            _ => ScopeOptions[0],
        };
        set
        {
            var scope = value == ScopeOptions[1] ? ConflictScopeSetting.All
                : value == ScopeOptions[2] ? ConflictScopeSetting.AiVsAi
                : ConflictScopeSetting.PlayerVsAi;
            _config.Update(c => c.Traffic.ConflictScope = scope);
            OnPropertyChanged();
        }
    }

    public bool AutoCull
    {
        get => _config.Current.Traffic.AutoCull;
        set
        {
            _config.Update(c => c.Traffic.AutoCull = value);
            OnPropertyChanged();
        }
    }

    public bool DryRun
    {
        get => _config.Current.Traffic.DryRun;
        set
        {
            _config.Update(c => c.Traffic.DryRun = value);
            OnPropertyChanged();
        }
    }

    public bool TrailsEnabled
    {
        get => _trailsEnabled;
        set
        {
            if (Set(ref _trailsEnabled, value))
            {
                PostToMap?.Invoke(JsonSerializer.Serialize(new { type = "trails", on = value }));
            }
        }
    }

    /// <summary>Follow-along data labels (altitude + groundspeed) under each marker.</summary>
    public bool LabelsEnabled
    {
        get => _labelsEnabled;
        set
        {
            if (Set(ref _labelsEnabled, value))
            {
                PostToMap?.Invoke(JsonSerializer.Serialize(new { type = "labels", on = value }));
            }
        }
    }

    // ----- window plumbing -----

    /// <summary>Called by the window for every message posted by the map page.</summary>
    public void HandleMapMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "ready":
                    _mapReady = true;
                    PostToMap?.Invoke(JsonSerializer.Serialize(new
                    {
                        type = "init",
                        token = _config.Current.Traffic.EffectiveMapboxToken,
                    }));
                    PostToMap?.Invoke(JsonSerializer.Serialize(new { type = "trails", on = _trailsEnabled }));
                    PostToMap?.Invoke(JsonSerializer.Serialize(new { type = "labels", on = _labelsEnabled }));
                    if (_latest is not null)
                    {
                        PostToMap?.Invoke(BuildSnapshotJson(_latest));
                    }
                    break;
                case "removeRequest":
                    if (doc.RootElement.TryGetProperty("callsign", out var cs) && cs.GetString() is { } callsign)
                    {
                        RequestRemoval(callsign, "map popup");
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Nothing actionable in a malformed page message.
        }
    }

    public void RequestRemoval(string callsign, string origin)
    {
        var detail = _latest?.Aircraft.FirstOrDefault(a => a.Callsign == callsign) is { } ac
            ? $"{ac.Type} · {ac.Alt:0} ft · {ac.IcaoFrom} → {ac.IcaoTo}"
            : "no longer in the feed";
        if (ConfirmRemoval?.Invoke(callsign, detail) != true)
        {
            return;
        }
        _ = _service.RequestRemovalAsync(callsign, $"manual via {origin}");
    }

    // ----- service events (already marshalled to the UI thread) -----

    private void OnSnapshot(TrafficSnapshot snapshot)
    {
        _latest = snapshot;
        SimTimeText = string.IsNullOrEmpty(snapshot.SimTime) ? "—" : snapshot.SimTime;
        RouteText = string.IsNullOrEmpty(snapshot.PlayerDepAirport) && string.IsNullOrEmpty(snapshot.PlayerArrAirport)
            ? "—"
            : $"{snapshot.PlayerDepAirport} → {snapshot.PlayerArrAirport}";
        CountText = $"{snapshot.Aircraft.Count} aircraft";

        Conflicts.Clear();
        foreach (var pair in snapshot.Conflicts.OrderByDescending(c => c.Severity))
        {
            var target = CullPolicy.SelectTarget(pair);
            Conflicts.Add(new ConflictRowViewModel
            {
                PairText = $"{pair.A.Callsign} ↔ {pair.B.Callsign}",
                DetailText = $"{pair.HorizontalNm:0.0} nm · {pair.VerticalFt:0} ft · closing {pair.ClosureKnots:0} kt",
                SeverityText = pair.Severity == ConflictSeverity.Conflict ? "CONFLICT" : "CAUTION",
                IsConflict = pair.Severity == ConflictSeverity.Conflict,
                InvolvesPlayer = pair.A.IsPlayer || pair.B.IsPlayer,
                TargetCallsign = target?.Callsign,
                RemoveCommand = new RelayCommand(_ =>
                {
                    if (target is not null)
                    {
                        RequestRemoval(target.Callsign, "conflict panel");
                    }
                }),
            });
        }

        if (_mapReady)
        {
            PostToMap?.Invoke(BuildSnapshotJson(snapshot));
        }
    }

    private static string BuildSnapshotJson(TrafficSnapshot snapshot)
    {
        // Player-involved pairs outrank AI-only ones so red always wins over blue.
        var severityByCallsign = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Conflicts)
        {
            var sev = SeverityKey(pair);
            foreach (var cs in new[] { pair.A.Callsign, pair.B.Callsign })
            {
                if (!severityByCallsign.TryGetValue(cs, out var existing) || Rank(sev) > Rank(existing))
                {
                    severityByCallsign[cs] = sev;
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            type = "snapshot",
            simTime = snapshot.SimTime,
            aircraft = snapshot.Aircraft.Select(a => new
            {
                callsign = a.Callsign,
                lat = a.Lat,
                lon = a.Lon,
                heading = a.Heading,
                alt = a.Alt,
                groundspeed = a.Groundspeed,
                type = a.Type,
                state = a.State,
                icaoFrom = a.IcaoFrom,
                icaoTo = a.IcaoTo,
                arrRunway = a.ArrRunway,
                onGround = a.OnGround,
                isPlayer = a.IsPlayer,
                sev = severityByCallsign.TryGetValue(a.Callsign, out var s) ? s : null,
            }),
            conflicts = snapshot.Conflicts.Select(c => new
            {
                a = c.A.Callsign,
                b = c.B.Callsign,
                severity = c.Severity == ConflictSeverity.Conflict ? "conflict" : "caution",
                player = c.A.IsPlayer || c.B.IsPlayer,
            }),
        });
    }

    private static string SeverityKey(ConflictPair pair)
        => (pair.Severity == ConflictSeverity.Conflict ? "conflict" : "caution")
           + (pair.A.IsPlayer || pair.B.IsPlayer ? "-player" : "-ai");

    private static int Rank(string severityKey) => severityKey switch
    {
        "conflict-player" => 3,
        "conflict-ai" => 2,
        "caution-player" => 1,
        _ => 0,
    };

    private void OnConnection(bool connected)
    {
        LinkConnected = connected;
        LinkStatusText = connected ? "Traffic link: connected" : "Traffic link: disconnected";
        if (!connected)
        {
            SimTimeText = "—";
            CountText = "0 aircraft";
            Conflicts.Clear();
        }
    }

    private void OnAction(TrafficActionLogEntry entry)
    {
        // UI keeps a window; the complete history is always in the Serilog file.
        ActionLog.Add(ActionLogRowViewModel.From(entry));
        while (ActionLog.Count > 500)
        {
            ActionLog.RemoveAt(0);
        }
    }

    private void ApplyThresholds()
    {
        if (!TryParse(ConflictHNm, out var chn) || !TryParse(ConflictVFt, out var cvf)
            || !TryParse(CautionHNm, out var uhn) || !TryParse(CautionVFt, out var uvf))
        {
            ThresholdStatus = "Thresholds must be positive numbers.";
            return;
        }
        if (uhn < chn || uvf < cvf)
        {
            ThresholdStatus = "Caution thresholds must be ≥ conflict thresholds.";
            return;
        }
        _config.Update(c =>
        {
            c.Traffic.ConflictHorizontalNm = chn;
            c.Traffic.ConflictVerticalFt = cvf;
            c.Traffic.CautionHorizontalNm = uhn;
            c.Traffic.CautionVerticalFt = uvf;
        });
        ThresholdStatus = "Applied.";
    }

    private static bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
