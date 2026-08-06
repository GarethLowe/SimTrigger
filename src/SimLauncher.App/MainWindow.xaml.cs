using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using SimLauncher.App.ViewModels;

namespace SimLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>Set true by App just before a real exit so Closing doesn't cancel it.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.EditDialog = ShowEditDialog;

        // Keep the session log scrolled to the newest line.
        ((INotifyCollectionChanged)viewModel.SessionLog).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        };
    }

    private bool ShowEditDialog(Core.Config.AppConfig draft, bool isExisting)
    {
        var dialog = new EditEventWindow(draft, isExisting, _viewModel) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // Tray app: closing the window just hides it.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
