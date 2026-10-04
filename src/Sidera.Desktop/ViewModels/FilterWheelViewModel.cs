using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A filter wheel card: connection, motion state, the filter in the light path, and a manual change to another one,
/// picked by name. Names are what the wheel reports; the slot index is shown as secondary text and is what is sent to
/// the wheel. A change goes through <c>DeviceOperationService</c>, which takes the wheel resource.
/// </summary>
public sealed partial class FilterWheelViewModel : DeviceViewModelBase
{
    private readonly IFilterWheel _wheel;
    private readonly IDisposable _motionSubscription;
    private readonly IDisposable _slotSubscription;

    public FilterWheelViewModel(IFilterWheel wheel, SideraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(wheel, host, postToUi, activity)
    {
        _wheel = wheel;
        CurrentSlot = wheel.CurrentSlot;
        _motionSubscription = host.EventBus.Subscribe<FilterWheelMotionStateChanged>((e, _) =>
        {
            if (e.DeviceId == wheel.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        _slotSubscription = host.EventBus.Subscribe<FilterWheelSlotChanged>((e, _) =>
        {
            if (e.DeviceId == wheel.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        SelectedFilter = wheel.CurrentSlot;
        Refresh();
    }

    /// <summary>The slots of the wheel, in order, to pick from by name.</summary>
    public IReadOnlyList<FilterSlot> Filters => _wheel.Slots;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoving))]
    [NotifyPropertyChangedFor(nameof(MotionText))]
    public partial FilterWheelMotionState MotionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFilterText))]
    [NotifyPropertyChangedFor(nameof(SlotText))]
    public partial FilterSlot CurrentSlot { get; private set; }

    /// <summary>The filter the user picked to change to.</summary>
    [ObservableProperty]
    public partial FilterSlot? SelectedFilter { get; set; }

    /// <summary>True while a manual change is running or waiting for the wheel.</summary>
    [ObservableProperty]
    public partial bool IsManualChangeRunning { get; private set; }

    public bool IsMoving => MotionState == FilterWheelMotionState.Moving;

    /// <summary>"Idle" or "Turning".</summary>
    public string MotionText => IsMoving ? "Turning" : "Idle";

    /// <summary>The name of the filter in the light path.</summary>
    public string CurrentFilterText => CurrentSlot.Name;

    /// <summary>The slot of that filter, for example "slot 4".</summary>
    public string SlotText => $"slot {CurrentSlot.Index}";

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangeAsync()
    {
        var target = SelectedFilter!;
        IsManualChangeRunning = true;
        RefreshCommands();
        try
        {
            await RunAsync(() => Host.DeviceOperations.MoveFilterWheelToAsync(Id, target.Index));
        }
        finally
        {
            IsManualChangeRunning = false;
            RefreshCommands();
        }
    }

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && MotionState == FilterWheelMotionState.Idle && !IsManualChangeRunning;

    private bool CanChange() =>
        !IsSequenceRunning && IsConnected && MotionState == FilterWheelMotionState.Idle && !IsManualChangeRunning
        && SelectedFilter is not null;

    partial void OnSelectedFilterChanged(FilterSlot? value) => ChangeCommand.NotifyCanExecuteChanged();

    protected override void RefreshDeviceState()
    {
        if (StateStore.TryGet(Id, out var state) && state?.FilterWheelMotionState is { } motion)
        {
            MotionState = motion;
            CurrentSlot = state.FilterSlot ?? _wheel.CurrentSlot;
        }
        else
        {
            MotionState = _wheel.MotionState;
            CurrentSlot = _wheel.CurrentSlot;
        }
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        ChangeCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() =>
        IsMoving ? new DeviceActivity("Turning", null, true) : new DeviceActivity($"{CurrentFilterText} · {SlotText}");

    public override void Dispose()
    {
        _motionSubscription.Dispose();
        _slotSubscription.Dispose();
        base.Dispose();
    }
}
