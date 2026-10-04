using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A guider card: connection, what it is doing, start and stop, and, for a guider with measurements, what it measures: the RMS, the star,
/// the settle, what the guider says about its setup and the history for the guide graph. The measurements arrive at the pace of the guide
/// camera; the view model passes them on at most about ten times a second, on the UI thread, and never holds more than the bounded history
/// of the guider.
/// </summary>
public sealed partial class GuiderViewModel : DeviceViewModelBase
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGuider _guider;
    private readonly IGuiderControl? _control;
    private readonly IDisposable _guidingSubscription;
    private long _lastPost;
    private int _posted;
    private bool _disposed;

    public GuiderViewModel(IGuider guider, SideraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(guider, host, postToUi, activity)
    {
        _guider = guider;
        _control = guider as IGuiderControl;
        _guidingSubscription = host.EventBus.Subscribe<GuidingStateChanged>((e, _) =>
        {
            if (e.DeviceId == guider.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        if (_control is not null)
        {
            _control.History.Changed += OnMeasurement;
            _control.StateChanged += OnMeasurement;
            _control.CapabilitiesChanged += OnMeasurement;
            GraphHistory = _control.History;
        }

        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuiding))]
    public partial GuidingState GuidingState { get; private set; }

    public bool IsGuiding => GuidingState == GuidingState.Guiding;

    public bool SupportsDither => _guider is IDitherGuider;

    public bool SupportsSettle => _guider is IGuidingSettler;

    public string DitherSupportText => SupportsDither ? "Supported" : "Not supported";

    public string SettleSupportText => SupportsSettle ? "Supported" : "Not supported";

    // ---- Measurements

    /// <summary>The guider measures (it has a history, a settle status and an RMS); the sections for that are shown.</summary>
    public bool HasMeasurements => _control is not null;

    public bool SupportsPause => _control?.Capabilities.Value is { CanPause: true };

    /// <summary>The guide samples the graph draws; <c>null</c> for a guider without measurements.</summary>
    public GuidingHistory? GraphHistory { get; }

    /// <summary>Counts the changes of the history; the graph draws again when it changes.</summary>
    [ObservableProperty]
    public partial long GraphVersion { get; private set; }

    /// <summary>How many seconds the graph shows: 60, 120 or 300.</summary>
    [ObservableProperty]
    public partial double GraphWindowSeconds { get; set; } = 120;

    public IReadOnlyList<double> GraphWindows { get; } = [60, 120, 300];

    /// <summary>"RA/Dec error in arcseconds" or, while the pixel scale is not known, in pixels.</summary>
    [ObservableProperty]
    public partial bool GraphInArcseconds { get; private set; } = true;

    /// <summary>The state of the guider in a word or two: Guiding, Calibrating, Settling, Star lost ...</summary>
    public string StateText => GuidingState switch
    {
        GuidingState.Idle => "Not guiding",
        GuidingState.StarSelected => "Star selected",
        GuidingState.StarLost => "Star lost",
        var other => other.ToString(),
    };

    [ObservableProperty]
    public partial string RmsText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string RmsRaText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string RmsDecText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string SnrText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string ExposureText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string PixelScaleText { get; private set; } = Unknown;

    /// <summary>Whether the guider is settling, and how far: "Stable", "Settling · 0.8 px", "Not settling".</summary>
    [ObservableProperty]
    public partial string SettleStateText { get; private set; } = "Not settling";

    [ObservableProperty]
    public partial string SettleToleranceText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string SettleStableText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string SettleTimeoutText { get; private set; } = Unknown;

    [ObservableProperty]
    public partial string LastSettleText { get; private set; } = "No settle yet";

    /// <summary>What the guider says about its setup: profile, guide camera, mount, calibration.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> SetupLines { get; private set; } = [];

    public bool HasSetup => SetupLines.Count > 0;

    private const string Unknown = "—";

    // ---- Commands

    [RelayCommand(CanExecute = nameof(CanStartGuiding))]
    private Task StartGuidingAsync() => RunAsync(() => Host.DeviceOperations.StartGuidingAsync(Id));

    // Stopping what is on its way (a start, a calibration, a settle) cannot wait for the lease that the start holds: it goes to the guider.
    [RelayCommand(CanExecute = nameof(CanStopGuiding))]
    private Task StopGuidingAsync() => RunAsync(() => GuidingState is GuidingState.Guiding or GuidingState.Looping or GuidingState.Paused or GuidingState.StarLost
        ? Host.DeviceOperations.StopGuidingAsync(Id)
        : _guider.StopGuidingAsync());

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => RunAsync(() => _control!.PauseGuidingAsync());

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => RunAsync(() => _control!.ResumeGuidingAsync());

    [RelayCommand]
    private void ClearGraph() => _control?.History.Clear();

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && GuidingState is not (GuidingState.Starting or GuidingState.Stopping or GuidingState.Calibrating
            or GuidingState.Settling or GuidingState.Dithering);

    private bool CanStartGuiding() =>
        !IsSequenceRunning && IsConnected && GuidingState is GuidingState.Idle or GuidingState.StarSelected or GuidingState.Looping;

    private bool CanStopGuiding() =>
        !IsSequenceRunning && IsConnected && GuidingState is not (GuidingState.Idle or GuidingState.StarSelected or GuidingState.Stopping);

    private bool CanPause() => !IsSequenceRunning && IsConnected && SupportsPause && GuidingState == GuidingState.Guiding;

    private bool CanResume() => !IsSequenceRunning && IsConnected && SupportsPause && GuidingState == GuidingState.Paused;

    partial void OnGraphWindowSecondsChanged(double value) => GraphVersion++;

    protected override void RefreshDeviceState()
    {
        GuidingState = StateStore.TryGet(Id, out var state) && state?.GuidingState is { } guiding
            ? guiding
            : _guider.GuidingState;
        OnPropertyChanged(nameof(StateText));
        RefreshMeasurements();
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        StartGuidingCommand.NotifyCanExecuteChanged();
        StopGuidingCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() => GuidingState switch
    {
        GuidingState.Guiding => new DeviceActivity("Guiding"),
        GuidingState.Dithering => new DeviceActivity("Dithering", null, true),
        GuidingState.Settling => new DeviceActivity("Settling", null, true),
        GuidingState.Calibrating => new DeviceActivity("Calibrating", null, true),
        GuidingState.Starting => new DeviceActivity("Starting guiding", null, true),
        GuidingState.Stopping => new DeviceActivity("Stopping guiding", null, true),
        GuidingState.Looping => new DeviceActivity("Looping"),
        GuidingState.StarSelected => new DeviceActivity("Star selected"),
        GuidingState.Paused => new DeviceActivity("Paused"),
        GuidingState.StarLost => new DeviceActivity("Star lost"),
        _ => new DeviceActivity("Idle"),
    };

    // A measurement arrives on the thread of the guider, many times a second: it is passed on at most every 100 ms, as one update of the UI.
    private void OnMeasurement(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.CompareExchange(ref _posted, 1, 0) != 0)
        {
            return;
        }

        var last = Interlocked.Read(ref _lastPost);
        var since = last == 0 ? TimeSpan.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(last);
        var wait = since >= RefreshInterval ? TimeSpan.Zero : RefreshInterval - since;
        if (wait > TimeSpan.Zero)
        {
            _ = Task.Delay(wait).ContinueWith(_ => PostRefresh(), TaskScheduler.Default);
        }
        else
        {
            PostRefresh();
        }
    }

    private void PostRefresh() => PostToUi(() =>
    {
        Interlocked.Exchange(ref _lastPost, System.Diagnostics.Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref _posted, 0);
        if (!_disposed)
        {
            RefreshMeasurements();
            RefreshCommandsOnly();
        }
    });

    private void RefreshCommandsOnly()
    {
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    private void RefreshMeasurements()
    {
        OnPropertyChanged(nameof(SupportsPause));
        if (_control is null)
        {
            return;
        }

        GraphVersion = _control.History.Version;
        var telemetry = _control.Telemetry;
        if (telemetry is null)
        {
            RmsText = RmsRaText = RmsDecText = SnrText = ExposureText = PixelScaleText = Unknown;
            SettleStateText = "Not settling";
            SettleToleranceText = SettleStableText = SettleTimeoutText = Unknown;
            LastSettleText = "No settle yet";
            SetupLines = [];
            OnPropertyChanged(nameof(HasSetup));
            return;
        }

        var rms = telemetry.Rms;
        RmsText = Arcsec(rms.TotalArcsec);
        RmsRaText = Arcsec(rms.RaArcsec);
        RmsDecText = Arcsec(rms.DecArcsec);
        SnrText = telemetry.StarSnr is { } snr ? snr.ToString("0.0", CultureInfo.InvariantCulture) : Unknown;
        ExposureText = telemetry.ExposureSeconds is { } exposure ? $"{exposure:0.##} s".ToString(CultureInfo.InvariantCulture) : Unknown;
        PixelScaleText = telemetry.PixelScaleArcsecPerPixel is { } scale ? string.Create(CultureInfo.InvariantCulture, $"{scale:0.##} \"/px") : Unknown;
        GraphInArcseconds = telemetry.PixelScaleArcsecPerPixel is not null;

        var settle = telemetry.Settle;
        SettleStateText = settle.IsActive
            ? settle.DistancePixels is { } distance ? string.Create(CultureInfo.InvariantCulture, $"Settling · {distance:0.00} px") : "Settling"
            : GuidingState == GuidingState.Guiding && settle.LastOutcome == GuidingSettleOutcome.Settled ? "Stable" : "Not settling";
        SettleToleranceText = settle.TolerancePixels is { } tolerance ? string.Create(CultureInfo.InvariantCulture, $"{tolerance:0.##} px") : Unknown;
        SettleStableText = settle.RequiredStable is { } stable ? string.Create(CultureInfo.InvariantCulture, $"{stable.TotalSeconds:0.#} s") : Unknown;
        SettleTimeoutText = settle.Timeout is { } timeout ? string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalSeconds:0.#} s") : Unknown;
        LastSettleText = settle.LastOutcome switch
        {
            GuidingSettleOutcome.Settled => string.Create(CultureInfo.InvariantCulture, $"Settled in {settle.LastDuration?.TotalSeconds:0.0} s"),
            GuidingSettleOutcome.TimedOut => $"Timed out · {settle.FailureReason}",
            GuidingSettleOutcome.Failed => $"Failed · {settle.FailureReason}",
            _ => "No settle yet",
        };

        var lines = new List<InfoLine>();
        if (_control.Info is { } info)
        {
            AddLine(lines, "Profile", info.Profile);
            AddLine(lines, "Guide camera", info.GuideCamera);
            AddLine(lines, "Mount", info.Mount);
            if (info.IsCalibrated is { } calibrated)
            {
                lines.Add(new InfoLine("Calibration", calibrated ? "Calibrated" : "Not calibrated"));
            }

            if (info.EquipmentConnected is { } connected)
            {
                lines.Add(new InfoLine("PHD2 equipment", connected ? "Connected" : "Not connected"));
            }
        }

        SetupLines = lines;
        OnPropertyChanged(nameof(HasSetup));
    }

    private static void AddLine(List<InfoLine> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add(new InfoLine(label, value));
        }
    }

    private static string Arcsec(double? value) =>
        value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:0.00}\"") : Unknown;

    public override void Dispose()
    {
        _disposed = true;
        if (_control is not null)
        {
            _control.History.Changed -= OnMeasurement;
            _control.StateChanged -= OnMeasurement;
            _control.CapabilitiesChanged -= OnMeasurement;
        }

        _guidingSubscription.Dispose();
        base.Dispose();
    }
}
