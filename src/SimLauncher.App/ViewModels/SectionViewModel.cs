using System.Collections.ObjectModel;
using SimLauncher.Core;

namespace SimLauncher.App.ViewModels;

/// <summary>A checkpoint section: header plus its event rows and an Add Event button.</summary>
public sealed class SectionViewModel : ObservableObject
{
    private bool _isActive;
    private bool _isCompleted;

    public SectionViewModel(Checkpoint checkpoint, Action<Checkpoint> onAddEvent)
    {
        Checkpoint = checkpoint;
        AddEventCommand = new RelayCommand(_ => onAddEvent(checkpoint));
    }

    public Checkpoint Checkpoint { get; }
    public string Title => Checkpoint.DisplayName();
    public ObservableCollection<AppRowViewModel> Rows { get; } = new();

    /// <summary>The checkpoint the session is currently at (header emphasised).</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    /// <summary>Checkpoint already passed this session (header ticked).</summary>
    public bool IsCompleted { get => _isCompleted; set => Set(ref _isCompleted, value); }

    public RelayCommand AddEventCommand { get; }
}
