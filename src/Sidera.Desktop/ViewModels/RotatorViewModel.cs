using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Rotators;
using Sidera.Runtime;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A rotator card: connection, motion, its position and manual moves (to a position, by an angle) and a halt. Every move is an explicit press of a button: connecting, choosing a rig, opening
/// the framing or solving never moves it. The position shown is the position of the rotator, never the rotation of the sky in an image. A move goes through
/// <c>DeviceOperationService</c>, which takes the rotator and the cameras of its rigs, so it waits for an exposure that is running; a halt takes nothing, so that it works while a move runs.
/// </summary>
public sealed partial class RotatorViewModel : DeviceViewModelBase
{
    private readonly IRotator _rotator;
    private readonly IDisposable _motionSubscription;
    private readonly IDisposable _positionSubscription;
    private CancellationTokenSource? _move;

    public RotatorViewModel(IRotator rotator, SideraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(rotator, host, postToUi, activity)
    {
        _rotator = rotator;
        _motionSubscription = host.EventBus.Subscribe<RotatorMotionStateChanged>((e, _) =>
        {
            if (e.DeviceId == rotator.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        _positionSubscription = host.EventBus.Subscribe<RotatorPositionChanged>((e, _) =>
        {
            if (e.DeviceId == rotator.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        TargetInput = Degrees(rotator.Position);
        Refresh();
    }

    private RotatorCapabilities? Capabilities => (_rotator as IRotatorControl)?.Capabilities.Value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoving), nameof(MotionText))]
    public partial RotatorMotionState MotionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double Position { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MechanicalText), nameof(ShowMechanical))]
    public partial double? MechanicalPosition { get; private set; }

    /// <summary>The absolute position to move to, in degrees, as typed.</summary>
    [ObservableProperty]
    public partial string TargetInput { get; set; } = string.Empty;

    /// <summary>The angle of the relative moves, in degrees, as typed.</summary>
    [ObservableProperty]
    public partial string RelativeInput { get; set; } = "5";

    /// <summary>A move was started by a button and has not ended.</summary>
    [ObservableProperty]
    public partial bool IsManualMoveRunning { get; private set; }

    public bool IsMoving => MotionState == RotatorMotionState.Moving;

    public string MotionText => IsMoving ? "Moving" : "Idle";

    /// <summary>"42.0°": where the rotator is, not how the sky looks in an image.</summary>
    public string PositionText => IsConnected || Position != 0 ? Degrees(Position) + "°" : "—";

    public string MechanicalText => MechanicalPosition is { } m ? Degrees(m) + "°" : "Not reported";

    // What the controls show follows what the rotator supports.
    public bool ShowAbsolute => Capabilities?.AbsoluteMove ?? true;

    public bool ShowRelative => Capabilities?.RelativeMove ?? false;

    public bool ShowMechanical => Capabilities?.HasMechanicalPosition == true;

    public bool ShowHalt => IsConnected;

    private static string Degrees(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);

    private static bool TryDegrees(string? text, out double value)
    {
        var trimmed = (text ?? string.Empty).Trim().Replace(',', '.').TrimEnd('°', ' ');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private Task MoveAsync()
    {
        ClearError();
        if (!TryDegrees(TargetInput, out var target))
        {
            ReportError(new FormatException("The position must be a number of degrees."));
            return Task.CompletedTask;
        }

        return MoveWithAsync(token => Host.DeviceOperations.MoveRotatorToAsync(Id, target, token));
    }

    /// <summary>A relative move by the angle in <see cref="RelativeInput"/>: "-" turns one way and "+" the other.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveBy))]
    private Task MoveByAsync(string? direction)
    {
        ClearError();
        if (!TryDegrees(RelativeInput, out var angle) || angle <= 0)
        {
            ReportError(new FormatException("The angle must be a number of degrees greater than zero."));
            return Task.CompletedTask;
        }

        var signed = direction == "-" ? -angle : angle;
        return MoveWithAsync(token => Host.DeviceOperations.MoveRotatorByAsync(Id, signed, token));
    }

    private async Task MoveWithAsync(Func<CancellationToken, Task> operation)
    {
        IsManualMoveRunning = true;
        _move = new CancellationTokenSource();
        RefreshCommands();
        var ticker = TickAsync(_move.Token);
        try
        {
            await RunAsync(() => operation(_move.Token));
        }
        finally
        {
            _move.Cancel();
            await ticker;
            _move.Dispose();
            _move = null;
            IsManualMoveRunning = false;
            RefreshCommands();
            PostToUi(Refresh);
        }
    }

    // The position follows a move that is running; it is only read, never written.
    private async Task TickAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token);
                PostToUi(Refresh);
            }
        }
        catch (OperationCanceledException)
        {
            // The move ended.
        }
    }

    /// <summary>Asks the rotator to stop. Always allowed while it is connected, and it does not wait for the move that is running.</summary>
    [RelayCommand(CanExecute = nameof(CanHalt))]
    private async Task HaltAsync()
    {
        ClearError();
        try
        {
            await Host.DeviceOperations.HaltRotatorAsync(Id);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    protected override bool CanDisconnect() => base.CanDisconnect() && !IsMoving && !IsManualMoveRunning;

    private bool CanMove() => !IsSequenceRunning && IsConnected && !IsMoving && !IsManualMoveRunning && ShowAbsolute;

    private bool CanMoveBy() => !IsSequenceRunning && IsConnected && !IsMoving && !IsManualMoveRunning && ShowRelative;

    private bool CanHalt() => IsConnected && (IsMoving || IsManualMoveRunning);

    protected override void RefreshDeviceState()
    {
        MotionState = _rotator.MotionState;
        Position = _rotator.Position;
        MechanicalPosition = (_rotator as IRotatorControl)?.MechanicalPosition;
        OnPropertyChanged(nameof(ShowAbsolute));
        OnPropertyChanged(nameof(ShowRelative));
        OnPropertyChanged(nameof(ShowMechanical));
        OnPropertyChanged(nameof(ShowHalt));
        OnPropertyChanged(nameof(PositionText));
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        MoveCommand.NotifyCanExecuteChanged();
        MoveByCommand.NotifyCanExecuteChanged();
        HaltCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() =>
        IsMoving ? new DeviceActivity($"Moving · {PositionText}", null, true) : new DeviceActivity($"Idle · {PositionText}");

    public override void Dispose()
    {
        _move?.Cancel();
        _motionSubscription.Dispose();
        _positionSubscription.Dispose();
        base.Dispose();
    }
}
