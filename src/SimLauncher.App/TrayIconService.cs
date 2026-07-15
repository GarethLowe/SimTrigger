using System.Drawing;
using System.Windows.Forms;
using SimLauncher.App.ViewModels;

namespace SimLauncher.App;

/// <summary>
/// System-tray icon (WinForms NotifyIcon — built into .NET, not a control library).
/// Context menu: Start Session, Stop/Teardown, profile selector, Open Window,
/// launch-on-startup toggle, Exit.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly Action _openWindow;
    private readonly Action _exit;
    private readonly ToolStripMenuItem _startStop;
    private readonly ToolStripMenuItem _profiles;
    private readonly ToolStripMenuItem _startup;

    public TrayIconService(MainViewModel viewModel, Action openWindow, Action exit)
    {
        _viewModel = viewModel;
        _openWindow = openWindow;
        _exit = exit;

        _startStop = new ToolStripMenuItem("Start Session", null, (_, _) => ToggleSession());
        _profiles = new ToolStripMenuItem("Profile");
        _startup = new ToolStripMenuItem("Launch on Windows startup")
        {
            Checked = StartupManager.IsEnabled(),
            CheckOnClick = true,
        };
        // Route through the view model so the main-window checkbox hears the change.
        _startup.CheckedChanged += (_, _) => _viewModel.LaunchOnStartup = _startup.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startStop);
        menu.Items.Add(_profiles);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Window", null, (_, _) => _openWindow()));
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _exit()));
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "SimLauncher",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _openWindow();
    }

    public void ShowBalloon(string title, string text)
        => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    private void ToggleSession()
    {
        _ = _viewModel.ToggleSessionAsync();
    }

    private void RefreshMenu()
    {
        _startStop.Text = _viewModel.IsSessionActive ? "Stop / Teardown" : _viewModel.SessionButtonText;
        _startStop.Enabled = _viewModel.SessionButtonEnabled;
        // Re-read in case the main-window checkbox changed it since the menu was built.
        _startup.Checked = StartupManager.IsEnabled();

        _profiles.DropDownItems.Clear();
        foreach (var profile in _viewModel.Profiles)
        {
            var item = new ToolStripMenuItem(profile)
            {
                Checked = profile == _viewModel.SelectedProfile,
                Enabled = !_viewModel.IsSessionActive,
            };
            item.Click += (_, _) => _viewModel.SelectedProfile = profile;
            _profiles.DropDownItems.Add(item);
        }
    }

    private static Icon CreateIcon()
    {
        // Same multi-size .ico that the exe and windows use (embedded WPF resource).
        var uri = new Uri("pack://application:,,,/Assets/app.ico");
        using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
