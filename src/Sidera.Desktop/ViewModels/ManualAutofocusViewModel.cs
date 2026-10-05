using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Focusing;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.ViewModels;

/// <summary>A sample of the focus curve for the table and the chart: the focuser position and the HFR measured there.</summary>
public sealed record FocusSampleRow(int Position, double Hfr)
{
    public string PositionText => Position.ToString(CultureInfo.InvariantCulture);

    public string HfrText => Hfr.ToString("0.00", CultureInfo.InvariantCulture) + " px";
}

/// <summary>
/// Starting the autofocus of a rig by hand, from the imaging page. It is the autofocus action of the sequences, run as a one step sequence through the sequence runner: the rig's camera and
/// focuser are taken by the runner as for any step (so it cannot overlap an exposure or a focuser move of a running session, and it does not hold anything else), the progress is what the
/// run publishes on the event bus, and the end result is what the action returns. Nothing is estimated here: the samples, the best position and the fit are the run's.
/// </summary>
public sealed partial class ManualAutofocusViewModel : ViewModelBase, IDisposable
{
    private readonly SideraRuntimeHost _host;
    private readonly Action<Action> _post;
    private readonly IDisposable _subscription;
    private CancellationTokenSource? _run;

    public ManualAutofocusViewModel(SideraRuntimeHost host, SequenceDraftDefaults defaults, Action<Action>? postToUi = null)
    {
        _host = host;
        _post = postToUi ?? (action => action());
        ExposureText = defaults.AutofocusExposureSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        StepSizeText = defaults.AutofocusStepSize.ToString(CultureInfo.InvariantCulture);
        SamplesText = defaults.AutofocusSampleCount.ToString(CultureInfo.InvariantCulture);
        _subscription = host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            _post(() => Apply(e));
            return Task.CompletedTask;
        });
        Refresh();
    }

    /// <summary>The rigs that can be focused: those with a focuser.</summary>
    public ObservableCollection<Rig> Rigs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRig), nameof(DisabledText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial Rig? SelectedRig { get; set; }

    [ObservableProperty]
    public partial string ExposureText { get; set; }

    [ObservableProperty]
    public partial string StepSizeText { get; set; }

    [ObservableProperty]
    public partial string SamplesText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(DisabledText))]
    public partial bool IsRunning { get; private set; }

    /// <summary>What the run is doing, from what it reported: "Sample 4 / 7 · HFR 2.11 px", "Fitting focus curve", "Focused at 19970 · HFR 1.82 px".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Where the focuser is now, or "—".</summary>
    [ObservableProperty]
    public partial string PositionText { get; private set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string BestFocusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BestHfrText { get; private set; } = string.Empty;

    /// <summary>How good the fit was, as far as the run says: the HFR of the fitted curve at the best position, the check afterwards and the number of passes.</summary>
    [ObservableProperty]
    public partial string FitText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = string.Empty;

    /// <summary>The samples of the run in the order they were taken.</summary>
    public ObservableCollection<FocusSampleRow> Samples { get; } = [];

    /// <summary>The position the run found as the best; <c>null</c> until it did.</summary>
    [ObservableProperty]
    public partial int? BestPosition { get; private set; }

    public bool HasRig => SelectedRig is not null;

    public bool HasResult => BestFocusText.Length > 0;

    public bool HasError => ErrorText.Length > 0;

    public bool HasSamples => Samples.Count > 0;

    /// <summary>Why a run cannot start now; empty when it can.</summary>
    public string DisabledText => ProblemToStart() ?? string.Empty;

    /// <summary>Reads the rigs and the focuser position again.</summary>
    public void Refresh()
    {
        var previous = SelectedRig?.Id;
        var focusable = _host.RigRegistry.GetAll().Where(r => r.FocuserId is not null).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!focusable.SequenceEqual(Rigs))
        {
            Rigs.Clear();
            foreach (var rig in focusable)
            {
                Rigs.Add(rig);
            }
        }

        SelectedRig = Rigs.FirstOrDefault(r => r.Id == previous) ?? Rigs.FirstOrDefault();
        RefreshPosition();
        OnPropertyChanged(nameof(DisabledText));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRigChanged(Rig? value) => RefreshPosition();

    /// <summary>Reads where the focuser of the selected rig is now.</summary>
    public void RefreshPosition()
    {
        PositionText = SelectedRig?.FocuserId is { } id && _host.DeviceRegistry.TryGet(id, out var device) && device is IFocuser { ConnectionState: DeviceConnectionState.Connected } focuser
            ? focuser.Position.ToString(CultureInfo.InvariantCulture)
            : "—";
    }

    // The camera and the focuser have to be connected, and nothing else may be using them.
    private string? ProblemToStart()
    {
        if (IsRunning)
        {
            return "Autofocus is running.";
        }

        if (SelectedRig is not { } rig)
        {
            return "No rig has a focuser. Give a rig a focuser on the Equipment page.";
        }

        if (_host.DeviceRegistry.TryGet(rig.CameraId, out var camera) && camera?.ConnectionState != DeviceConnectionState.Connected)
        {
            return $"Connect the camera of {rig.Name} on the Equipment page.";
        }

        if (!_host.DeviceRegistry.TryGet(rig.FocuserId!.Value, out var focuser) || focuser.ConnectionState != DeviceConnectionState.Connected)
        {
            return $"Connect the focuser of {rig.Name} on the Equipment page.";
        }

        if (_host.ResourceManager.IsHeld(ResourceId.ForDevice(rig.CameraId)) || _host.ResourceManager.IsHeld(ResourceId.ForDevice(rig.FocuserId!.Value)))
        {
            return "The camera or the focuser is in use. Wait for it to finish.";
        }

        return null;
    }

    private bool CanStart() => ProblemToStart() is null;

    /// <summary>The options of the run, or why the entries are not usable.</summary>
    public bool TryBuildOptions(out AutofocusOptions options, out string? problem)
    {
        options = default!;
        problem = null;
        if (!double.TryParse(ExposureText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds <= 0)
        {
            problem = "Exposure must be a number of seconds greater than 0.";
            return false;
        }

        if (!int.TryParse(StepSizeText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var step) || step <= 0)
        {
            problem = "Step size must be a whole number of focuser steps greater than 0.";
            return false;
        }

        if (!int.TryParse(SamplesText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples) || samples < AutofocusOptions.MinimumSampleCount || samples > AutofocusOptions.MaximumSampleCount)
        {
            problem = $"Samples must be a whole number from {AutofocusOptions.MinimumSampleCount} to {AutofocusOptions.MaximumSampleCount}.";
            return false;
        }

        options = new AutofocusOptions(TimeSpan.FromSeconds(seconds), step, samples);
        return true;
    }

    /// <summary>Focuses the selected rig: samples around the current position, fits the curve, moves to the best position and checks it.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedRig is not { } rig)
        {
            return;
        }

        ErrorText = string.Empty;
        if (!TryBuildOptions(out var options, out var problem))
        {
            ErrorText = problem!;
            return;
        }

        Samples.Clear();
        OnPropertyChanged(nameof(HasSamples));
        BestPosition = null;
        BestFocusText = BestHfrText = FitText = string.Empty;
        var source = new CancellationTokenSource();
        _run = source;
        IsRunning = true;
        StatusText = "Starting…";
        AutofocusResult? result = null;
        try
        {
            var action = AutofocusAction.ForRig(
                _host.DeviceRegistry, rig, options, _host.FocusMetricProvider, _host.EventBus, _host.LoggerFactory.CreateLogger<AutofocusAction>(), _host.AcquisitionDefaults);
            var runner = new SequenceRunner(_host.ResourceManager, _host.SafePointCoordinator, _host.LoggerFactory.CreateLogger<SequenceRunner>());
            runner.StepCompleted += (_, e) =>
            {
                if (e.Result.Payload is AutofocusResult done)
                {
                    result = done;
                }
            };
            await runner.RunAsync(new Sequence("Autofocus", [action]), source.Token);
            if (result is { } found)
            {
                BestPosition = found.BestPosition;
                BestFocusText = found.BestPosition.ToString(CultureInfo.InvariantCulture);
                BestHfrText = found.BestHfr.ToString("0.00", CultureInfo.InvariantCulture) + " px";
                FitText = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Fitted HFR {found.FittedHfr:0.00} px · {(found.Verification is { } check ? $"check at {check.FocuserPosition}: HFR {check.Hfr:0.00} px · " : string.Empty)}{found.Attempts} {(found.Attempts == 1 ? "pass" : "passes")} · from {found.InitialPosition}");
                StatusText = $"Focused at {found.BestPosition}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorText = ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
            StatusText = "Autofocus failed.";
        }
        finally
        {
            _run = null;
            source.Dispose();
            IsRunning = false;
            RefreshPosition();
            OnPropertyChanged(nameof(DisabledText));
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        try
        {
            _run?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run just ended.
        }
    }

    // What the run publishes, for the rig that is being focused.
    private void Apply(AutofocusProgressChanged e)
    {
        if (!IsRunning || SelectedRig is not { } rig || e.RigId != rig.Id)
        {
            return;
        }

        var p = e.Progress;
        switch (p.Phase)
        {
            case AutofocusPhase.Measuring when p.SampleIndex == 0:
                if (p.Attempt == 1)
                {
                    Samples.Clear();
                }

                StatusText = string.Create(CultureInfo.InvariantCulture, $"Sampling {p.SampleCount} focus positions{(p.Attempt > 1 ? $" · pass {p.Attempt}" : string.Empty)}");
                break;
            case AutofocusPhase.Measuring:
                if (p.Position is { } position && p.Hfr is { } hfr)
                {
                    Samples.Add(new FocusSampleRow(position, hfr));
                    PositionText = position.ToString(CultureInfo.InvariantCulture);
                    StatusText = string.Create(CultureInfo.InvariantCulture, $"Sample {p.SampleIndex} / {p.SampleCount} · HFR {hfr:0.00} px");
                }

                break;
            case AutofocusPhase.Fitting:
                StatusText = "Fitting focus curve";
                break;
            case AutofocusPhase.Moving:
                StatusText = "Moving to best focus";
                break;
            case AutofocusPhase.Verifying:
                StatusText = "Checking the focus";
                break;
        }

        OnPropertyChanged(nameof(HasSamples));
    }

    public void Dispose() => _subscription.Dispose();
}
