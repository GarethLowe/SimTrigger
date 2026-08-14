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

namespace SimLauncher.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private TrayIconService? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Without this, a UI-thread exception kills the process with nothing in our own
        // log — a past window crash was only visible in Windows Event Viewer.
        DispatcherUnhandledException += (_, args) =>
            Serilog.Log.Fatal(args.Exception, "Unhandled dispatcher exception; process will terminate");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Serilog.Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception; process will terminate");

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimLauncher", "logs");

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((_, cfg) => cfg
                .MinimumLevel.Debug()
                .WriteTo.Logger(lc => lc
                    .WriteTo.File(Path.Combine(logDir, "simlauncher-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.Sink(UiLogSink.Instance)))
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
                services.AddSingleton<LocalApi>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Start();

        var config = _host.Services.GetRequiredService<ConfigStore>();
        var errors = config.Load();
        config.StartWatching();

        var coordinator = _host.Services.GetRequiredService<SessionCoordinator>();
        coordinator.Initialize();
        var api = _host.Services.GetRequiredService<LocalApi>();
        // /show arrives on a listener thread; OpenWindow touches WPF, so hop the dispatcher.
        api.ShowWindow = () => Dispatcher.Invoke(OpenWindow);
        api.Start();

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
