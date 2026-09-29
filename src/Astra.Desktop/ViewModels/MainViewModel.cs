using System;
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
    private readonly SequenceRunner _sequenceRunner = new();
    private readonly TimeSpan _requestedExposure;
    private readonly TimeSpan _sequenceExposure;
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _exposureSubscription;
    private CancellationTokenSource? _sequenceCts;

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
        TimeSpan? sequenceExposureDuration = null
    )
    {
        _camera = camera;
        _stateStore = host.StateStore;
        _deviceRegistry = host.DeviceRegistry;
        _postToUi = postToUi;
        _requestedExposure = exposureDuration ?? TimeSpan.FromSeconds(5);
        _sequenceExposure = sequenceExposureDuration ?? TimeSpan.FromSeconds(2);

        RefreshState();
        RefreshSequence();

        _camera.ExposureProgressChanged += OnExposureProgressChanged;
        _sequenceRunner.Changed += OnSequenceChanged;
        _connectionSubscription = host.EventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
        _exposureSubscription = host.EventBus.Subscribe<CameraExposureStateChanged>(OnExposureStateChanged);
    }

    public string CameraName => _camera.Name;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartExposureCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunSequenceCommand))]
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
    public partial double ExposureProgress { get; private set; }

    /// <summary>Raw result of the last successful exposure; <c>null</c> until there is one.</summary>
    [ObservableProperty]
    public partial CameraFrame? LastFrame { get; private set; }

    [ObservableProperty]
    public partial SequenceState SequenceState { get; private set; }

    [ObservableProperty]
    public partial string? CurrentStepName { get; private set; }

    /// <summary>For example "Step 2 / 5"; empty until the first step has started.</summary>
    [ObservableProperty]
    public partial string SequenceStepText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int SequenceStepCount { get; private set; }

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

    public bool IsExposing => ExposureState == CameraExposureState.Exposing;

    public string ExposureSummary =>
        $"{ExposureState} · {ExposureElapsed.TotalSeconds:0.0} / {ExposureDuration.TotalSeconds:0.0} s";

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => _camera.ConnectAsync();

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => _camera.DisconnectAsync();

    [RelayCommand(CanExecute = nameof(CanStartExposure))]
    private async Task StartExposureAsync()
    {
        LastFrame = await _camera.ExposeAsync(_requestedExposure);
    }

    [RelayCommand(CanExecute = nameof(CanRunSequence))]
    private async Task RunSequenceAsync()
    {
        var sequence = BuildDemoSequence();
        SequenceStepCount = sequence.Steps.Count;
        SequenceError = null;

        var cts = new CancellationTokenSource();
        _sequenceCts = cts;

        try
        {
            // Starts synchronously and flips the runner to Running before the first await.
            var run = _sequenceRunner.RunAsync(sequence, cts.Token);
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
        var id = _camera.Id;
        // Equipment connection is separate from sequences: the user connects the camera first.
        return new Sequence("Demo", [
            new CameraExposureAction(_deviceRegistry, id, _sequenceExposure),
            new CameraExposureAction(_deviceRegistry, id, _sequenceExposure),
            new CameraExposureAction(_deviceRegistry, id, _sequenceExposure),
        ]);
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
        _sequenceRunner.Changed -= OnSequenceChanged;
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

    private void RefreshSequence()
    {
        SequenceState = _sequenceRunner.State;
        CurrentStepName = _sequenceRunner.CurrentStepName;
        SequenceStepText = _sequenceRunner.CurrentStepIndex < 0
            ? string.Empty
            : $"Step {_sequenceRunner.CurrentStepIndex + 1} / {SequenceStepCount}";
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
