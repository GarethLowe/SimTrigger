using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SimLauncher.App.ViewModels;
using SimLauncher.Core;
using SimLauncher.Core.Config;
using ShutdownMode = SimLauncher.Core.Config.ShutdownMode;

namespace SimLauncher.App;

public partial class EditEventWindow : Window
{
    private readonly AppConfig _draft;

    public EditEventWindow(AppConfig draft, bool isExisting, MainViewModel viewModel)
    {
        InitializeComponent();
        _draft = draft;

        foreach (var cp in Enum.GetValues<Checkpoint>())
        {
            CheckpointBox.Items.Add(new ComboBoxItem { Content = cp.DisplayName(), Tag = cp });
        }
        CheckpointBox.SelectedIndex = (int)draft.Checkpoint;

        WaitForBox.Items.Add(new ComboBoxItem { Content = "(none)", Tag = null });
        foreach (var name in viewModel.GetAppNames(draft.Checkpoint, draft.Name))
        {
            WaitForBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        WaitForBox.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(draft.WaitForApp))
        {
            foreach (ComboBoxItem item in WaitForBox.Items)
            {
                if (string.Equals(item.Tag as string, draft.WaitForApp, StringComparison.OrdinalIgnoreCase))
                {
                    WaitForBox.SelectedItem = item;
                    break;
                }
            }
        }

        NameBox.Text = draft.Name;
        PathBox.Text = draft.Path;
        ArgsBox.Text = draft.Args;
        if (draft.DelaySeconds >= 60 && draft.DelaySeconds % 60 == 0)
        {
            DelayBox.Text = (draft.DelaySeconds / 60).ToString();
            DelayUnitBox.SelectedIndex = 1;
        }
        else
        {
            DelayBox.Text = draft.DelaySeconds.ToString();
            DelayUnitBox.SelectedIndex = 0;
        }
        ReadySecondsBox.Text = draft.WaitForAppReadySeconds.ToString();
        ShutdownBox.SelectedIndex = draft.Shutdown switch
        {
            null => 0,
            ShutdownMode.Graceful => 1,
            ShutdownMode.Kill => 2,
            ShutdownMode.Leave => 3,
            _ => 0,
        };
        TimeoutBox.Text = draft.ShutdownTimeoutSeconds.ToString();
        AlreadyRunningBox.SelectedIndex = (int)draft.AlreadyRunning;
        RestartBox.IsChecked = draft.RestartIfCrashed;
        ShellBox.IsChecked = draft.ShellExecute;
        AdminBox.IsChecked = draft.RunAsAdmin;

        DeleteButton.Visibility = isExisting ? Visibility.Visible : Visibility.Collapsed;
        Title = isExisting ? $"Edit — {draft.Name}" : "Add Event";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select application",
        };
        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(PathBox.Text))
        {
            ShowError("Name and path are required.");
            return;
        }
        if (!int.TryParse(DelayBox.Text.Trim(), out var delay) || delay < 0)
        {
            ShowError("Delay must be a non-negative whole number.");
            return;
        }
        if (!int.TryParse(ReadySecondsBox.Text.Trim(), out var ready) || ready < 0)
        {
            ShowError("Ready seconds must be a non-negative whole number.");
            return;
        }
        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 0)
        {
            ShowError("Shutdown timeout must be a non-negative whole number.");
            return;
        }

        _draft.Name = NameBox.Text.Trim();
        _draft.Path = PathBox.Text.Trim();
        _draft.Args = ArgsBox.Text.Trim();
        _draft.Checkpoint = (Checkpoint)((ComboBoxItem)CheckpointBox.SelectedItem).Tag!;
        // Canonical unit is seconds; the selector is UI-only.
        _draft.DelaySeconds = DelayUnitBox.SelectedIndex == 1 ? delay * 60 : delay;
        _draft.WaitForApp = ((ComboBoxItem)WaitForBox.SelectedItem)?.Tag as string;
        _draft.WaitForAppReadySeconds = ready;
        _draft.Shutdown = ShutdownBox.SelectedIndex switch
        {
            1 => ShutdownMode.Graceful,
            2 => ShutdownMode.Kill,
            3 => ShutdownMode.Leave,
            _ => null,
        };
        _draft.ShutdownTimeoutSeconds = timeout;
        _draft.AlreadyRunning = (AlreadyRunningBehavior)AlreadyRunningBox.SelectedIndex;
        _draft.RestartIfCrashed = RestartBox.IsChecked == true;
        _draft.ShellExecute = ShellBox.IsChecked == true;
        _draft.RunAsAdmin = AdminBox.IsChecked == true;

        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Remove '{_draft.Name}' from this profile?", "Delete event",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _draft.Name = MainViewModel.DeleteSentinel.Name;
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
