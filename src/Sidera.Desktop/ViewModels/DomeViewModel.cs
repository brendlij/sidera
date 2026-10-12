using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Runtime;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A dome card, or the card of a roll-off roof (a dome that has a shutter and no azimuth): connection, the state of the shutter, the azimuth, and manual moves (open and close the shutter, turn, park,
/// find home) with a halt. Every move is an explicit press of a button: connecting, discovering or choosing a dome never moves it. Moves go through <c>DeviceOperationService</c>; a halt takes no
/// resource, so that it works while a move runs. Real equipment asks first (<see cref="HardwareSafetyViewModel"/>): opening a shutter every time, the other moves once for each dome; closing the
/// shutter and halting never ask, because closed and stopped are the safe directions.
/// </summary>
public sealed partial class DomeViewModel : DeviceViewModelBase
{
    private readonly IDome _dome;
    private readonly IDisposable _shutterSubscription;
    private readonly IDisposable _stateSubscription;
    private readonly Func<HardwareSafetyViewModel?> _safety;
    private CancellationTokenSource? _operation;

    public DomeViewModel(IDome dome, SideraRuntimeHost host, Action<Action> postToUi, SessionActivity activity, Func<HardwareSafetyViewModel?>? safety = null)
        : base(dome, host, postToUi, activity)
    {
        _dome = dome;
        _safety = safety ?? (() => null);
        _shutterSubscription = host.EventBus.Subscribe<DomeShutterStateChanged>((e, _) =>
        {
            if (e.DeviceId == dome.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        _stateSubscription = host.EventBus.Subscribe<DomeStateChanged>((e, _) =>
        {
            if (e.DeviceId == dome.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        Refresh();
    }

    private DomeCapabilities? Capabilities => (_dome as IDomeControl)?.Capabilities.Value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShutterText), nameof(IsShutterMoving), nameof(ShutterKind))]
    public partial DomeShutterState ShutterState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AzimuthText))]
    public partial double? Azimuth { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MotionText))]
    public partial bool IsMoving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParkText))]
    public partial bool? AtPark { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeText))]
    public partial bool? AtHome { get; private set; }

    /// <summary>The azimuth to turn to, in degrees, as typed.</summary>
    [ObservableProperty]
    public partial string TargetInput { get; set; } = "0";

    /// <summary>A move was started by a button and has not ended.</summary>
    [ObservableProperty]
    public partial bool IsOperationRunning { get; private set; }

    // ---- what is shown

    public string ShutterText => ShutterState switch
    {
        DomeShutterState.Open => "Open",
        DomeShutterState.Closed => "Closed",
        DomeShutterState.Opening => "Opening…",
        DomeShutterState.Closing => "Closing…",
        DomeShutterState.Error => "Error: the shutter reports an error",
        _ => "Not reported",
    };

    /// <summary>How the shutter is drawn: open is a state to look at, error is a problem.</summary>
    public StatusKind ShutterKind => ShutterState switch
    {
        DomeShutterState.Open => StatusKind.Warning,
        DomeShutterState.Closed => StatusKind.Ok,
        DomeShutterState.Opening or DomeShutterState.Closing => StatusKind.Active,
        DomeShutterState.Error => StatusKind.Error,
        _ => StatusKind.Neutral,
    };

    public bool IsShutterMoving => ShutterState is DomeShutterState.Opening or DomeShutterState.Closing;

    public string AzimuthText => Azimuth is { } azimuth ? azimuth.ToString("0.0#", CultureInfo.InvariantCulture) + "°" : "Not reported";

    public string MotionText => IsMoving ? "Moving" : "Idle";

    public string ParkText => AtPark is { } parked ? (parked ? "Parked" : "Not parked") : "Not reported";

    public string HomeText => AtHome is { } home ? (home ? "At home" : "Not at home") : "Not reported";

    /// <summary>"Roll-off roof" for a shutter without azimuth, "Dome" otherwise (and while it is not connected: nothing is assumed).</summary>
    public string KindText => Capabilities?.IsShutterOnly == true ? "Roll-off roof" : "Dome";

    public bool IsRoof => Capabilities?.IsShutterOnly == true;

    public string NotesText => Capabilities is { Notes.Count: > 0 } c ? string.Join(' ', c.Notes) : string.Empty;

    public string DriverText => Capabilities?.Driver is { } driver
        ? string.Join(" · ", new[] { driver.Name, driver.DriverInfo, driver.DriverVersion is { } v ? "version " + v : null, driver.InterfaceVersion is { } i ? "interface " + i : null }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : string.Empty;

    // What the controls show follows what the device says it supports.
    public bool ShowShutter => Capabilities?.CanSetShutter ?? false;

    public bool ShowAzimuth => Capabilities?.CanSetAzimuth ?? false;

    public bool ShowPark => Capabilities?.CanPark ?? false;

    public bool ShowHome => Capabilities?.CanFindHome ?? false;

    public bool ShowHalt => IsConnected;

    // ---- moves

    [RelayCommand(CanExecute = nameof(CanOpenShutter))]
    private async Task OpenShutterAsync()
    {
        if (!await ConfirmAsync("Open the dome shutter", remember: false))
        {
            return;
        }

        await RunMoveAsync(token => Host.DeviceOperations.OpenDomeShutterAsync(Id, token));
    }

    [RelayCommand(CanExecute = nameof(CanCloseShutter))]
    private Task CloseShutterAsync() => RunMoveAsync(token => Host.DeviceOperations.CloseDomeShutterAsync(Id, token));

    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task SlewAsync()
    {
        ClearError();
        if (!TryDegrees(TargetInput, out var azimuth) || azimuth < 0 || azimuth >= 360)
        {
            ReportError(new FormatException("The azimuth must be a number of degrees from 0 up to 360."));
            return;
        }

        if (!await ConfirmAsync("Turn the dome", remember: true))
        {
            return;
        }

        await RunMoveAsync(token => Host.DeviceOperations.SlewDomeToAsync(Id, azimuth, token));
    }

    [RelayCommand(CanExecute = nameof(CanPark))]
    private async Task ParkAsync()
    {
        if (!await ConfirmAsync("Park the dome", remember: true))
        {
            return;
        }

        await RunMoveAsync(token => Host.DeviceOperations.ParkDomeAsync(Id, token));
    }

    [RelayCommand(CanExecute = nameof(CanFindHome))]
    private async Task FindHomeAsync()
    {
        if (!await ConfirmAsync("Send the dome home", remember: true))
        {
            return;
        }

        await RunMoveAsync(token => Host.DeviceOperations.FindDomeHomeAsync(Id, token));
    }

    /// <summary>Asks the dome to stop everything that moves. Always allowed while it is connected, and it does not wait for the move that is running.</summary>
    [RelayCommand(CanExecute = nameof(CanHalt))]
    private async Task HaltAsync()
    {
        ClearError();
        try
        {
            await Host.DeviceOperations.HaltDomeAsync(Id);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private async Task<bool> ConfirmAsync(string what, bool remember)
    {
        if (_safety() is not { } safety)
        {
            return true;
        }

        var notice = HardwareSafetyViewModel.NoticeFor(Host.DeviceRegistry, MovingEquipment.Dome, Id);
        return await safety.ConfirmAsync(what, [notice], remember);
    }

    private async Task RunMoveAsync(Func<CancellationToken, Task> operation)
    {
        IsOperationRunning = true;
        _operation = new CancellationTokenSource();
        RefreshCommands();
        try
        {
            await RunAsync(() => operation(_operation.Token));
        }
        finally
        {
            _operation.Dispose();
            _operation = null;
            IsOperationRunning = false;
            RefreshCommands();
            PostToUi(Refresh);
        }
    }

    private static bool TryDegrees(string? text, out double value)
    {
        var trimmed = (text ?? string.Empty).Trim().Replace(',', '.').TrimEnd('°', ' ');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    protected override bool CanDisconnect() => base.CanDisconnect() && !IsMoving && !IsOperationRunning;

    private bool Idle() => !IsSequenceRunning && IsConnected && !IsOperationRunning && !IsMoving;

    private bool CanOpenShutter() => Idle() && ShowShutter && ShutterState is not (DomeShutterState.Open or DomeShutterState.Opening);

    private bool CanCloseShutter() => Idle() && ShowShutter && ShutterState is not (DomeShutterState.Closed or DomeShutterState.Closing);

    private bool CanSlew() => Idle() && ShowAzimuth;

    private bool CanPark() => Idle() && ShowPark;

    private bool CanFindHome() => Idle() && ShowHome;

    private bool CanHalt() => IsConnected && (IsMoving || IsOperationRunning);

    protected override void RefreshDeviceState()
    {
        var telemetry = (_dome as IDomeControl)?.Telemetry;
        ShutterState = _dome.ShutterState;
        Azimuth = _dome.Azimuth;
        IsMoving = _dome.IsMoving;
        AtPark = telemetry?.AtPark;
        AtHome = telemetry?.AtHome;
        foreach (var name in new[] { nameof(KindText), nameof(IsRoof), nameof(NotesText), nameof(DriverText), nameof(ShowShutter), nameof(ShowAzimuth), nameof(ShowPark), nameof(ShowHome), nameof(ShowHalt) })
        {
            OnPropertyChanged(name);
        }
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        OpenShutterCommand.NotifyCanExecuteChanged();
        CloseShutterCommand.NotifyCanExecuteChanged();
        SlewCommand.NotifyCanExecuteChanged();
        ParkCommand.NotifyCanExecuteChanged();
        FindHomeCommand.NotifyCanExecuteChanged();
        HaltCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity()
    {
        var shutter = ShutterState == DomeShutterState.Unknown ? string.Empty : "shutter " + ShutterText.ToLowerInvariant();
        var azimuth = Azimuth is null ? string.Empty : AzimuthText;
        var line = string.Join(" · ", new[] { IsMoving ? "Moving" : "Idle", shutter, azimuth }.Where(s => s.Length > 0));
        return new DeviceActivity(line, null, IsMoving);
    }

    public override void Dispose()
    {
        _operation?.Cancel();
        _shutterSubscription.Dispose();
        _stateSubscription.Dispose();
        base.Dispose();
    }
}
