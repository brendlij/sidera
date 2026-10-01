using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;
using Astra.Runtime.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ICamera _camera;
    private readonly StateStore _stateStore;
    private readonly Action<Action> _postToUi;
    private readonly DeviceRegistry _deviceRegistry;
    private readonly DeviceOperationService _operations;
    private readonly SequenceRunner _sequenceRunner;
    private readonly TimeSpan _requestedExposure;
    private readonly TimeSpan _sequenceExposure;
    private readonly TimeSpan _sequenceDelay;
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _exposureSubscription;
    private CancellationTokenSource? _sequenceCts;
    private CancellationTokenSource? _exposureCts;

    /// <param name="camera">Must be registered in the host's registry for the demo sequence to find it.</param>
    /// <param name="postToUi">
    /// Marshals an action onto the UI thread. EventBus handlers run on whichever thread
    /// published the event, so property changes must not happen directly in them.
    /// </param>
    public MainViewModel(
        ICamera camera,
        AstraRuntimeHost host,
        Action<Action> postToUi,
        TimeSpan? exposureDuration = null,
        TimeSpan? sequenceExposureDuration = null,
        TimeSpan? sequenceDelayDuration = null
    )
    {
        _camera = camera;
        _stateStore = host.StateStore;
        _deviceRegistry = host.DeviceRegistry;
        _operations = host.DeviceOperations;
        _sequenceRunner = new SequenceRunner(host.ResourceManager);
        _postToUi = postToUi;
        _requestedExposure = exposureDuration ?? TimeSpan.FromSeconds(5);
        _sequenceExposure = sequenceExposureDuration ?? TimeSpan.FromSeconds(2);
        _sequenceDelay = sequenceDelayDuration ?? TimeSpan.FromSeconds(1);

        DemoSequence = BuildDemoSequence();
        SequenceOutline = SequenceOutlineItem.From(DemoSequence);

        RefreshState();
        RefreshSequence();

        _camera.ExposureProgressChanged += OnExposureProgressChanged;
        _sequenceRunner.Changed += OnSequenceChanged;
        _sequenceRunner.StepCompleted += OnSequenceStepCompleted;
        _connectionSubscription = host.EventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
        _exposureSubscription = host.EventBus.Subscribe<CameraExposureStateChanged>(OnExposureStateChanged);
    }

    public string CameraName => _camera.Name;

    public string CameraType => _camera.Type.ToString();

    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

    public string ExposureProgressText => ExposureProgress.ToString("P0");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSequenceCommand))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    public partial DeviceConnectionState ConnectionState { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSequenceCommand))]
    [NotifyPropertyChangedFor(nameof(IsExposing))]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial CameraExposureState ExposureState { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial TimeSpan ExposureElapsed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureSummary))]
    public partial TimeSpan ExposureDuration { get; private set; }

    /// <summary>From 0.0 to 1.0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExposureProgressText))]
    public partial double ExposureProgress { get; private set; }

    /// <summary>Raw result of the last successful exposure; <c>null</c> until there is one.</summary>
    [ObservableProperty]
    public partial CameraFrame? LastFrame { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSequenceCompleted))]
    [NotifyPropertyChangedFor(nameof(IsSequenceCancelled))]
    [NotifyPropertyChangedFor(nameof(IsSequenceFailed))]
    public partial SequenceState SequenceState { get; private set; }

    /// <summary>Name of the innermost step that is running (or ran last), e.g. "Exposure 2s".</summary>
    [ObservableProperty]
    public partial string? CurrentStepName { get; private set; }

    /// <summary>The containers around the running step and the step itself, outermost first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SequenceStatusLine> SequenceStatusLines { get; private set; } = [];

    public bool IsSequenceCompleted => SequenceState == SequenceState.Completed;
    public bool IsSequenceCancelled => SequenceState == SequenceState.Cancelled;
    public bool IsSequenceFailed => SequenceState == SequenceState.Failed;

    /// <summary>Read-only outline of <see cref="DemoSequence"/> for display.</summary>
    public IReadOnlyList<SequenceOutlineItem> SequenceOutline { get; }

    /// <summary>
    /// One line per branch that is running right now (several with a parallel step), each the full path from the
    /// top-level step to the running step. Empty when nothing runs.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<SequenceStatusLine> ActiveSequenceStatusLines { get; private set; } = [];

    /// <summary>
    /// <see cref="SequenceStatusLines"/> on one line, e.g. "Repeat × 3 · 2 / 3 › Imaging Block › Exposure 2s";
    /// empty until the first step has started.
    /// </summary>
    [ObservableProperty]
    public partial string SequenceStepText { get; private set; } = string.Empty;

    /// <summary>The predefined demonstration sequence: Repeat × 3 of an "Imaging Block" group holding an exposure and a wait. Reused for every run.</summary>
    public Sequence DemoSequence { get; }

    /// <summary>Message of the exception that ended the last sequence run; <c>null</c> otherwise.</summary>
    [ObservableProperty]
    public partial string? SequenceError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSequenceCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSequenceCommand))]
    public partial bool IsSequenceRunning { get; private set; }

    /// <summary>True while an exposure started with the manual Start Exposure command is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelExposureCommand))]
    public partial bool IsManualExposureRunning { get; private set; }

    public bool IsExposing => ExposureState == CameraExposureState.Exposing;

    public string ExposureSummary =>
        $"{ExposureState} · {ExposureElapsed.TotalSeconds:0.0} / {ExposureDuration.TotalSeconds:0.0} s";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => _operations.ConnectAsync(_camera.Id);

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => _operations.DisconnectAsync(_camera.Id);

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private async Task StartExposureAsync()
    {
        var cts = new CancellationTokenSource();
        _exposureCts = cts;
        IsManualExposureRunning = true;

        try
        {
            LastFrame = await _operations.ExposeAsync(_camera.Id, _requestedExposure, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user: the camera is idle again and no frame was produced.
        }
        finally
        {
            _exposureCts = null;
            cts.Dispose();
            IsManualExposureRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsManualExposureRunning))]
    private void CancelExposure()
    {
        try
        {
            _exposureCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The exposure just ended.
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunSequence))]
    private async Task RunSequenceAsync()
    {
        SequenceError = null;

        var cts = new CancellationTokenSource();
        _sequenceCts = cts;

        try
        {
            // Starts synchronously and flips the runner to Running before the first await.
            var run = _sequenceRunner.RunAsync(DemoSequence, cts.Token);
            RefreshSequence();
            await run;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user; the runner state is Cancelled.
        }
        catch (Exception ex)
        {
            SequenceError = ex.Message;
        }
        finally
        {
            _sequenceCts = null;
            cts.Dispose();
            RefreshSequence();
        }
    }

    [RelayCommand(CanExecute = nameof(IsSequenceRunning))]
    private void CancelSequence()
    {
        try
        {
            _sequenceCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sequence just ended.
        }
    }

    private Sequence BuildDemoSequence()
    {
        // Equipment connection is separate from sequences: the user connects the camera first.
        // One group of an exposure and a wait, executed three times by the repeat.
        var exposure = new CameraExposureAction(_deviceRegistry, _camera.Id, _sequenceExposure);
        var block = new SequenceGroup("Imaging Block", [exposure, new DelayAction(_sequenceDelay)]);
        return new Sequence("Demo", [new RepeatStep(3, block)]);
    }

    private bool CanConnect() =>
        !IsSequenceRunning && ConnectionState == DeviceConnectionState.Disconnected;

    private bool CanDisconnect() =>
        !IsSequenceRunning
        && ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    private bool CanStartExposure() =>
        !IsSequenceRunning
        && ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    private bool CanRunSequence() =>
        !IsSequenceRunning
        && ConnectionState == DeviceConnectionState.Connected
        && ExposureState == CameraExposureState.Idle;

    public void Dispose()
    {
        CancelSequence();
        CancelExposure();
        _sequenceRunner.Changed -= OnSequenceChanged;
        _sequenceRunner.StepCompleted -= OnSequenceStepCompleted;
        _camera.ExposureProgressChanged -= OnExposureProgressChanged;
        _connectionSubscription.Dispose();
        _exposureSubscription.Dispose();
    }

    private Task OnConnectionStateChanged(
        DeviceConnectionStateChanged e,
        CancellationToken cancellationToken
    ) => PostRefreshFor(e.DeviceId);

    private Task OnExposureStateChanged(
        CameraExposureStateChanged e,
        CancellationToken cancellationToken
    ) => PostRefreshFor(e.DeviceId);

    // Raised on the sequence's thread; only at state and step changes, never per exposure tick.
    private void OnSequenceChanged(object? sender, EventArgs e)
    {
        _postToUi(RefreshSequence);
    }

    // Raised on the sequence's thread once per successfully completed step.
    private void OnSequenceStepCompleted(object? sender, SequenceStepCompletedEventArgs e)
    {
        if (e.Result.Payload is CameraFrame frame)
        {
            _postToUi(() => LastFrame = frame);
        }
    }

    private void RefreshSequence()
    {
        SequenceState = _sequenceRunner.State;
        var position = _sequenceRunner.CurrentPosition;
        CurrentStepName = position?.StepName;
        var lines = SequenceStatusLine.From(position);
        var text = string.Join(" › ", lines.Select(line => line.Text));
        if (text != SequenceStepText)
        {
            SequenceStatusLines = lines;
            SequenceStepText = text;
        }

        var active = SequenceStatusLine.ForActiveBranches(_sequenceRunner.ActivePositions);
        if (!active.Select(l => l.Text).SequenceEqual(ActiveSequenceStatusLines.Select(l => l.Text)))
        {
            ActiveSequenceStatusLines = active;
        }

        IsSequenceRunning = _sequenceRunner.IsRunning;
    }

    // Raised on the camera's thread, roughly 5 times per second during an exposure.
    private void OnExposureProgressChanged(object? sender, EventArgs e)
    {
        _postToUi(RefreshProgress);
    }

    private void RefreshProgress()
    {
        ExposureElapsed = _camera.ExposureElapsed;
        ExposureDuration = _camera.ExposureDuration ?? TimeSpan.Zero;
        ExposureProgress = _camera.ExposureProgress;
    }

    private Task PostRefreshFor(DeviceId deviceId)
    {
        if (deviceId == _camera.Id)
        {
            _postToUi(RefreshState);
        }

        return Task.CompletedTask;
    }

    private void RefreshState()
    {
        if (_stateStore.TryGet(_camera.Id, out var state))
        {
            ConnectionState = state!.ConnectionState;
            ExposureState = state.ExposureState ?? CameraExposureState.Idle;
        }
        else
        {
            ConnectionState = _camera.ConnectionState;
            ExposureState = _camera.ExposureState;
        }

        RefreshProgress();
    }
}
