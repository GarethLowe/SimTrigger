using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SimLauncher.App.ViewModels;

namespace SimLauncher.App;

public partial class TrafficWindow : Window
{
    private const string VirtualHost = "traffic.simlauncher";

    private readonly TrafficViewModel _viewModel;

    /// <summary>Set by App just before a real exit so Closing doesn't cancel it.</summary>
    public bool AllowClose { get; set; }

    public TrafficWindow(TrafficViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.ConfirmRemoval = (callsign, detail) => MessageBox.Show(this,
            $"Remove {callsign}?\n\n{detail}\n\nThe aircraft is despawned by BeyondATC and disappears from TCAS and scenery.",
            "Remove aircraft", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        viewModel.PostToMap += json =>
        {
            if (MapView.CoreWebView2 is not null)
            {
                MapView.CoreWebView2.PostWebMessageAsJson(json);
            }
        };

        ((INotifyCollectionChanged)viewModel.ActionLog).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && ActionLogList.Items.Count > 0)
            {
                ActionLogList.ScrollIntoView(ActionLogList.Items[ActionLogList.Items.Count - 1]);
            }
        };

        Loaded += async (_, _) => await InitializeWebViewAsync();
    }

    private bool _webViewInitialized;

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitialized)
        {
            return;
        }
        _webViewInitialized = true;

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimLauncher", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await MapView.EnsureCoreWebView2Async(environment);

        var mapFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "TrafficMap");
        MapView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, mapFolder, CoreWebView2HostResourceAccessKind.Allow);
        MapView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        MapView.CoreWebView2.WebMessageReceived += (_, e) => _viewModel.HandleMapMessage(e.WebMessageAsJson);
        MapView.CoreWebView2.Navigate($"https://{VirtualHost}/map.html");
    }

    private void OnActionLogKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.C
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            CopySelectedLogRows();
            e.Handled = true;
        }
    }

    private void OnCopySelectedLog(object sender, RoutedEventArgs e) => CopySelectedLogRows();

    private void OnCopyAllLog(object sender, RoutedEventArgs e) => CopyLogRows(_viewModel.ActionLog);

    private void CopySelectedLogRows()
    {
        // Filter the source list so rows come out in log order, not click order.
        var selected = ActionLogList.SelectedItems.Cast<ActionLogRowViewModel>().ToHashSet();
        CopyLogRows(_viewModel.ActionLog.Where(selected.Contains));
    }

    private static void CopyLogRows(IEnumerable<ActionLogRowViewModel> rows)
    {
        var text = string.Join(Environment.NewLine, rows.Select(r => r.ClipboardText));
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Keep the map alive in the background so reopening is instant.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Only reached on a real close (app exit). An undisposed WebView2 can keep
        // its browser-process connection alive and hang WPF shutdown.
        MapView.Dispose();
        base.OnClosed(e);
    }
}
