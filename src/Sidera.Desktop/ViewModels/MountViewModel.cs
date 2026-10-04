using System;
using System.Globalization;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>A mount card: connection, motion state, coordinates and a manual slew.</summary>
public sealed partial class MountViewModel : DeviceViewModelBase
{
    private readonly IMount _mount;
    private readonly IDisposable _motionSubscription;

    public MountViewModel(IMount mount, SideraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(mount, host, postToUi, activity)
    {
        _mount = mount;
        _motionSubscription = host.EventBus.Subscribe<MountMotionStateChanged>((e, _) =>
        {
            if (e.DeviceId == mount.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlewing))]
    public partial MountMotionState MotionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    public partial CelestialCoordinates Coordinates { get; private set; } = new(0, 0);

    /// <summary>
    /// Right ascension in hours as typed by the user (0 up to, but not including, 24). It starts empty, is filled from where the mount
    /// points when that is known, and is never filled with a made-up 0.
    /// </summary>
    [ObservableProperty]
    public partial string RightAscensionInput { get; set; } = string.Empty;

    /// <summary>Declination in degrees as typed by the user (-90 to +90); empty until the position of the mount is known.</summary>
    [ObservableProperty]
    public partial string DeclinationInput { get; set; } = string.Empty;

    /// <summary>Where the mount points is known (it is connected and said so); only then is there a target to start from.</summary>
    [ObservableProperty]
    public partial bool IsPositionKnown { get; private set; }

    /// <summary>Why the slew cannot be started, in a sentence; empty when the target is valid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSlewProblem))]
    public partial string SlewProblemText { get; private set; } = string.Empty;

    public bool HasSlewProblem => SlewProblemText.Length > 0;

    /// <summary>The slew asked for is a big move and waits for a second click.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingLargeSlew { get; private set; }

    /// <summary>What the big move is: from where to where, and how far.</summary>
    [ObservableProperty]
    public partial string LargeSlewText { get; private set; } = string.Empty;

    private CelestialCoordinates? _pendingSlew;
    private bool _targetEdited;
    private bool _filling;

    partial void OnRightAscensionInputChanged(string value) => TargetChanged();

    partial void OnDeclinationInputChanged(string value) => TargetChanged();

    private void TargetChanged()
    {
        if (!_filling)
        {
            _targetEdited = true;
        }

        CancelLargeSlew();
        UpdateSlewProblem();
        SlewCommand.NotifyCanExecuteChanged();
    }

    // Fills the target from where the mount points, once it is known and only while the user has not typed their own.
    private void FillTargetFromMount(bool wasKnown)
    {
        if (_targetEdited || (wasKnown && RightAscensionInput.Length > 0))
        {
            return;
        }

        _filling = true;
        try
        {
            RightAscensionInput = IsPositionKnown ? FormatTarget(Coordinates.RightAscensionHours) : string.Empty;
            DeclinationInput = IsPositionKnown ? FormatTarget(Coordinates.DeclinationDegrees) : string.Empty;
        }
        finally
        {
            _filling = false;
        }
    }

    private void UpdateSlewProblem() =>
        SlewProblemText = !IsPositionKnown
            ? "Where the mount points is not known yet, so there is no target to start from. Connect the mount."
            : TryReadTarget(out _, out var problem) ? string.Empty : problem;

    private bool TryReadTarget(out CelestialCoordinates target, out string problem)
    {
        target = null!;
        if (!TryNumber(RightAscensionInput, out var ra))
        {
            problem = "Right ascension must be a number of hours.";
            return false;
        }

        if (!TryNumber(DeclinationInput, out var dec))
        {
            problem = "Declination must be a number of degrees.";
            return false;
        }

        if (ra < 0 || ra >= 24)
        {
            problem = "Right ascension must be from 0 up to (not including) 24 hours.";
            return false;
        }

        if (dec < -90 || dec > 90)
        {
            problem = "Declination must be from -90 to +90 degrees.";
            return false;
        }

        target = new CelestialCoordinates(ra, dec);
        problem = string.Empty;
        return true;
    }

    // Numbers only: "NaN" and "Infinity" parse as doubles and are not numbers a mount can be sent to.
    private static bool TryNumber(string? text, out double value)
    {
        var trimmed = text?.Trim();
        value = 0;
        return !string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value);
    }

    private static string FormatTarget(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>True while a manual slew is running or waiting for the mount.</summary>
    [ObservableProperty]
    public partial bool IsManualSlewRunning { get; private set; }

    public bool IsSlewing => MotionState == MountMotionState.Slewing;

    public string CoordinatesText => string.Create(
        CultureInfo.InvariantCulture,
        $"RA {Coordinates.RightAscensionHours:0.###} h · Dec {Coordinates.DeclinationDegrees:+0.##;-0.##;0}°");

    // A target that is near the mount goes at once; a far one is shown first and waits for ConfirmSlew.
    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task SlewAsync()
    {
        ClearError();
        if (!TryReadTarget(out var target, out var problem))
        {
            ReportError(problem);
            return;
        }

        if (SlewSafety.IsLargeSlew(Coordinates, target))
        {
            _pendingSlew = target;
            LargeSlewText = string.Create(
                CultureInfo.InvariantCulture,
                $"This is a large slew: {SlewSafety.SeparationDegrees(Coordinates, target):0} degrees from {Describe(Coordinates)} to {Describe(target)}.");
            IsConfirmingLargeSlew = true;
            return;
        }

        await StartSlewAsync(target);
    }

    [RelayCommand]
    private async Task ConfirmSlewAsync()
    {
        var target = _pendingSlew;
        CancelLargeSlew();
        if (target is not null && CanSlew())
        {
            await StartSlewAsync(target);
        }
    }

    [RelayCommand]
    private void CancelSlew() => CancelLargeSlew();

    private void CancelLargeSlew()
    {
        _pendingSlew = null;
        IsConfirmingLargeSlew = false;
        LargeSlewText = string.Empty;
    }

    private async Task StartSlewAsync(CelestialCoordinates target)
    {
        IsManualSlewRunning = true;
        RefreshCommands();
        try
        {
            await RunAsync(() => Host.DeviceOperations.SlewToAsync(Id, target));
        }
        finally
        {
            IsManualSlewRunning = false;
            RefreshCommands();
        }
    }

    private static string Describe(CelestialCoordinates c) => string.Create(
        CultureInfo.InvariantCulture, $"RA {c.RightAscensionHours:0.###} h, Dec {c.DeclinationDegrees:+0.##;-0.##;0}°");

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && MotionState != MountMotionState.Slewing && !IsManualSlewRunning;

    private bool CanSlew() =>
        !IsSequenceRunning && IsConnected && MotionState != MountMotionState.Slewing && !IsManualSlewRunning
        && IsPositionKnown && TryReadTarget(out _, out _);

    protected override void RefreshDeviceState()
    {
        var wasKnown = IsPositionKnown;
        if (StateStore.TryGet(Id, out var state) && state?.MotionState is { } motion)
        {
            MotionState = motion;
            Coordinates = state.Coordinates ?? _mount.Coordinates;
        }
        else
        {
            MotionState = _mount.MotionState;
            Coordinates = _mount.Coordinates;
        }

        // A mount that is not connected, or one that has not reported a position, has no position: the default 0 / 0 of a device
        // that was never asked is not one.
        IsPositionKnown = IsConnected && (_mount is not IMountControl control || control.Telemetry?.Coordinates is not null);
        FillTargetFromMount(wasKnown && IsPositionKnown);
        UpdateSlewProblem();
        SlewCommand.NotifyCanExecuteChanged();
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        SlewCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() => MotionState switch
    {
        MountMotionState.Slewing => new DeviceActivity("Slewing", null, true),
        MountMotionState.Tracking => new DeviceActivity("Tracking"),
        _ => new DeviceActivity("Idle"),
    };

    public override void Dispose()
    {
        _motionSubscription.Dispose();
        base.Dispose();
    }
}
