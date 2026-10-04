using System;
using System.Globalization;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Mounts;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>A mount card: connection, motion state, coordinates and a manual slew.</summary>
public sealed partial class MountViewModel : DeviceViewModelBase
{
    private readonly IMount _mount;
    private readonly IDisposable _motionSubscription;

    public MountViewModel(IMount mount, AstraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
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

        RightAscensionInput = Format(mount.Coordinates.RightAscensionHours);
        DeclinationInput = Format(mount.Coordinates.DeclinationDegrees);
        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlewing))]
    public partial MountMotionState MotionState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    public partial CelestialCoordinates Coordinates { get; private set; } = new(0, 0);

    /// <summary>Right ascension in hours as typed by the user (0 up to, but not including, 24).</summary>
    [ObservableProperty]
    public partial string RightAscensionInput { get; set; } = "0";

    /// <summary>Declination in degrees as typed by the user (-90 to +90).</summary>
    [ObservableProperty]
    public partial string DeclinationInput { get; set; } = "0";

    /// <summary>True while a manual slew is running or waiting for the mount.</summary>
    [ObservableProperty]
    public partial bool IsManualSlewRunning { get; private set; }

    public bool IsSlewing => MotionState == MountMotionState.Slewing;

    public string CoordinatesText => string.Create(
        CultureInfo.InvariantCulture,
        $"RA {Coordinates.RightAscensionHours:0.###} h · Dec {Coordinates.DeclinationDegrees:+0.##;-0.##;0}°");

    [RelayCommand(CanExecute = nameof(CanSlew))]
    private async Task SlewAsync()
    {
        ClearError();

        CelestialCoordinates target;
        try
        {
            // The domain type validates the ranges; this only turns the typed text into numbers.
            target = new CelestialCoordinates(
                ParseNumber(RightAscensionInput, "Right ascension (hours)"),
                ParseNumber(DeclinationInput, "Declination (degrees)"));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            ReportError(ex);
            return;
        }

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

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && MotionState != MountMotionState.Slewing && !IsManualSlewRunning;

    private bool CanSlew() =>
        !IsSequenceRunning && IsConnected && MotionState != MountMotionState.Slewing && !IsManualSlewRunning;

    protected override void RefreshDeviceState()
    {
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

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    // Accepts the decimal separator of the user's culture as well as a dot.
    private static double ParseNumber(string? text, string what)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)))
        {
            return value;
        }

        throw new FormatException($"{what} must be a number.");
    }

    public override void Dispose()
    {
        _motionSubscription.Dispose();
        base.Dispose();
    }
}
