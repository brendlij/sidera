using System;
using System.Globalization;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// A focuser card: connection, motion state, position and a manual move to an absolute position. There is no focusing
/// here: autofocus builds on the focuser later. A move goes through <c>DeviceOperationService</c>, which takes the
/// focuser resource, so it keeps apart from sequences even if the UI did not.
/// </summary>
public sealed partial class FocuserViewModel : DeviceViewModelBase
{
    private readonly IFocuser _focuser;
    private readonly IDisposable _motionSubscription;
    private readonly IDisposable _positionSubscription;

    public FocuserViewModel(IFocuser focuser, AstraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(focuser, host, postToUi, activity)
    {
        _focuser = focuser;
        _motionSubscription = host.EventBus.Subscribe<FocuserMotionStateChanged>((e, _) =>
        {
            if (e.DeviceId == focuser.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        _positionSubscription = host.EventBus.Subscribe<FocuserPositionChanged>((e, _) =>
        {
            if (e.DeviceId == focuser.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        TargetInput = focuser.Position.ToString(CultureInfo.InvariantCulture);
        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoving))]
    [NotifyPropertyChangedFor(nameof(MotionText))]
    public partial FocuserMotionState MotionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial int Position { get; private set; }

    /// <summary>The absolute position to move to, as typed by the user.</summary>
    [ObservableProperty]
    public partial string TargetInput { get; set; } = string.Empty;

    /// <summary>True while a manual move is running or waiting for the focuser.</summary>
    [ObservableProperty]
    public partial bool IsManualMoveRunning { get; private set; }

    public bool IsMoving => MotionState == FocuserMotionState.Moving;

    /// <summary>"Idle" or "Moving".</summary>
    public string MotionText => IsMoving ? "Moving" : "Idle";

    /// <summary>The focuser can only move by steps: it has no position, no range and no target to move to.</summary>
    public bool IsRelative => !_focuser.IsAbsolute;

    public bool IsAbsolute => _focuser.IsAbsolute;

    public string PositionText => IsRelative
        ? "No position (relative focuser)"
        : string.Create(CultureInfo.InvariantCulture, $"{Position} steps");

    /// <summary>What the focuser can move to, for example "0 to 50000 steps".</summary>
    public string RangeText => IsRelative
        ? "None (relative focuser)"
        : string.Create(CultureInfo.InvariantCulture, $"{_focuser.MinPosition} to {_focuser.MaxPosition} steps");

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        ClearError();

        var text = TargetInput?.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var target)
            && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
        {
            ReportError(new FormatException("Focuser position must be a whole number."));
            return;
        }

        IsManualMoveRunning = true;
        RefreshCommands();
        try
        {
            await RunAsync(() => Host.DeviceOperations.MoveFocuserToAsync(Id, target));
        }
        finally
        {
            IsManualMoveRunning = false;
            RefreshCommands();
        }
    }

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && MotionState == FocuserMotionState.Idle && !IsManualMoveRunning;

    private bool CanMove() =>
        !IsSequenceRunning && IsConnected && _focuser.IsAbsolute && MotionState == FocuserMotionState.Idle && !IsManualMoveRunning;

    protected override void RefreshDeviceState()
    {
        OnPropertyChanged(nameof(IsRelative));
        OnPropertyChanged(nameof(IsAbsolute));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(RangeText));
        if (StateStore.TryGet(Id, out var state) && state?.FocuserMotionState is { } motion)
        {
            MotionState = motion;
            Position = state.FocuserPosition ?? _focuser.Position;
        }
        else
        {
            MotionState = _focuser.MotionState;
            Position = _focuser.Position;
        }
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        MoveCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() =>
        IsMoving ? new DeviceActivity($"Moving · {PositionText}", null, true) : new DeviceActivity($"Idle · {PositionText}");

    public override void Dispose()
    {
        _motionSubscription.Dispose();
        _positionSubscription.Dispose();
        base.Dispose();
    }
}
