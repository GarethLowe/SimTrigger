using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SimLauncher.App.ViewModels;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using SimLauncher.Core.Engine;
using SimLauncher.Core.Processes;
using SimLauncher.SimConnect;
using SimLauncher.Traffic;

namespace SimLauncher.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private TrafficWindow? _trafficWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimLauncher", "logs");

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, cfg) => cfg
                .MinimumLevel.Debug()
                .WriteTo.Logger(lc => lc
                    // The per-tick detection trace (Debug) is high-volume and lives only in
                    // the CLEF file; Information+ detection events (conflict lifecycle,
                    // player-eligibility warnings) still reach the main log and UI.
                    .Filter.ByExcluding(e =>
                        Serilog.Filters.Matching.FromSource(TrafficMonitorService.DetectionLoggerName)(e)
                        && e.Level < Serilog.Events.LogEventLevel.Information)
                    .WriteTo.File(Path.Combine(logDir, "simlauncher-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Sink(UiLogSink.Instance))
                // Structured detection log (CLEF/newline-delimited JSON): every tick, every
                // near-pair gate decision, eligibility transitions. UTC @t plus the feed's
                // SimTime property allow cross-correlation with BeyondATC's own logs.
                .WriteTo.Logger(lc => lc
                    .Filter.ByIncludingOnly(
                        Serilog.Filters.Matching.FromSource(TrafficMonitorService.DetectionLoggerName))
                    .WriteTo.File(new Serilog.Formatting.Compact.CompactJsonFormatter(),
                        Path.Combine(logDir, "traffic-.clef"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7)))
            .ConfigureServices(services =>
            {
                services.AddSingleton<ConfigStore>();
                services.AddSingleton<IProcessManager, WindowsProcessManager>();
                services.AddSingleton<SessionStateMachine>();
                services.AddSingleton<CheckpointEngine>();
                services.AddSingleton<ISimStateSource>(sp => new SimConnectStateSource(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SimConnectStateSource>>(),
                    () => sp.GetRequiredService<ConfigStore>().Current.SimConnection));
                services.AddSingleton<SessionCoordinator>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<ConfigStore>().Current.Traffic;
                    Uri.TryCreate(config.WebSocketUrl, UriKind.Absolute, out var uri);
                    return new TrafficMonitorService(
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                        uri);
                });
                services.AddSingleton<TrafficViewModel>();
            })
            .Build();

        _host.Start();

        var config = _host.Services.GetRequiredService<ConfigStore>();
        var errors = config.Load();
        config.StartWatching();

        var coordinator = _host.Services.GetRequiredService<SessionCoordinator>();
        coordinator.Initialize();

        // The traffic link runs independently of process management: if BeyondATC
        // isn't up it just shows "disconnected" and keeps retrying.
        var traffic = _host.Services.GetRequiredService<TrafficMonitorService>();
        ApplyTrafficSettings(traffic, config.Current);
        config.ConfigChanged += cfg => ApplyTrafficSettings(traffic, cfg);
        traffic.Start();

        _window = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        _tray = new TrayIconService(viewModel, OpenWindow, ExitApp);

        if (errors.Count > 0)
        {
            OpenWindow(); // surface config problems immediately
        }
        else
        {
            _tray.ShowBalloon("SimLauncher", "Running in the tray. Double-click to open.");
        }
    }

    private static void ApplyTrafficSettings(TrafficMonitorService traffic, LauncherConfig config)
    {
        var t = config.Traffic;
        traffic.UpdateSettings(
            new ConflictThresholds(t.ConflictHorizontalNm, t.ConflictVerticalFt,
                t.CautionHorizontalNm, t.CautionVerticalFt),
            new AutoCullOptions
            {
                Enabled = t.AutoCull,
                DryRun = t.DryRun,
                SustainSeconds = t.AutoCullSustainSeconds,
                CooldownSeconds = t.AutoCullCooldownSeconds,
            },
            t.ConflictScope switch
            {
                ConflictScopeSetting.All => ConflictScope.All,
                ConflictScopeSetting.AiVsAi => ConflictScope.AiVsAi,
                _ => ConflictScope.PlayerVsAi,
            });
    }

    public void OpenTrafficWindow()
    {
        _trafficWindow ??= new TrafficWindow(_host!.Services.GetRequiredService<TrafficViewModel>());
        _trafficWindow.Show();
        if (_trafficWindow.WindowState == WindowState.Minimized)
        {
            _trafficWindow.WindowState = WindowState.Normal;
        }
        _trafficWindow.Activate();
    }

    private void OpenWindow()
    {
        if (_window is null)
        {
            return;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private bool _exiting;

    private async void ExitApp()
    {
        if (_exiting)
        {
            return;
        }
        var coordinator = _host?.Services.GetService<SessionCoordinator>();
        var stopFirst = false;
        if (coordinator?.IsSessionActive == true)
        {
            var result = MessageBox.Show(
                "A session is active. Stop it and shut down managed apps before exiting?",
                "SimLauncher", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel)
            {
                return;
            }
            stopFirst = result == MessageBoxResult.Yes;
        }

        _exiting = true;
        if (stopFirst)
        {
            try
            {
                // Awaited, not blocked on: a sync wait here deadlocks the dispatcher
                // that teardown continuations may need, which froze exit entirely.
                await coordinator!.StopSessionAsync();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Teardown on exit failed; exiting anyway");
            }
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_window is not null)
        {
            _window.AllowClose = true;
        }
        if (_trafficWindow is not null)
        {
            _trafficWindow.AllowClose = true;
            _trafficWindow.Close();
        }
        _tray?.Dispose();
        if (_host is { } host)
        {
            // Off-thread so any dispatcher-bound continuation can't deadlock the exit.
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(3))).GetAwaiter().GetResult();
            host.Dispose();
        }
        base.OnExit(e);
    }
}
