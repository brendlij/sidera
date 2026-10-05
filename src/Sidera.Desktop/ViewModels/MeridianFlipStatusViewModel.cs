using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Mounts;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// What the flip of one mount is doing, for the status of the session: the state and a sentence about it ("Waiting for Main, Wide ready", "Solving #2 · error 24"") and, when a failed flip waits
/// for the user, the two ways out. It follows the <see cref="MeridianFlipGroup"/> of the run and tells nothing of its own.
/// </summary>
public sealed partial class MeridianFlipStatusViewModel : ObservableObject
{
    private readonly MeridianFlipGroup _group;

    public MeridianFlipStatusViewModel(MeridianFlipGroup group, string mountName)
    {
        _group = group;
        MountName = mountName;
        Refresh();
    }

    public string MountName { get; }

    /// <summary>"Main, Wide": the setups that hold for this flip.</summary>
    public string SetupsText => string.Join(", ", _group.SetupNames);

    [ObservableProperty]
    public partial MeridianFlipState State { get; private set; }

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    /// <summary>The flip has failed and the setups of this mount are held until the user retries or aborts.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(AbortCommand))]
    public partial bool IsWaitingForDecision { get; private set; }

    /// <summary>The flip is doing something now (it has left Monitoring, and is neither done nor failed).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    public string Title => $"Meridian flip · {MountName}";

    /// <summary>Reads the state of the group again.</summary>
    public void Refresh()
    {
        State = _group.State;
        Message = _group.Message;
        IsWaitingForDecision = _group.IsWaitingForDecision;
        IsActive = State is not (MeridianFlipState.Monitoring or MeridianFlipState.Completed);
    }

    private bool CanDecide() => IsWaitingForDecision;

    /// <summary>Tries the failed flip again: the mount is flipped and centered as before.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Retry() => _group.Retry();

    /// <summary>Ends the session after a failed flip. Nothing is reversed on the mount.</summary>
    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Abort() => _group.Abort();
}
