using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Sidera.Core.Devices;
using Sidera.Runtime;
using Sidera.Runtime.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// What every device card shows and does: identity, connection state, connect and disconnect. State is read from
/// the <see cref="StateStore"/> after the event bus reports a change; changes arrive on any thread and are posted to
/// the UI thread. Connect and disconnect go through <c>DeviceOperationService</c>, so the runtime stays the boundary
/// that keeps conflicting operations apart; disabled buttons are only a courtesy.
/// </summary>
public abstract partial class DeviceViewModelBase : ViewModelBase, IDisposable
{
    /// <summary>What a device is doing: the words, how far it is (when that is known) and whether it counts as busy.</summary>
    protected readonly record struct DeviceActivity(string? Text, double? Progress = null, bool Busy = false);

    private readonly IDevice _device;
    private readonly SessionActivity _activity;
    private readonly IDisposable _connectionSubscription;

    protected DeviceViewModelBase(
        IDevice device,
        SideraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity
    )
    {
        _device = device;
        Host = host;
        PostToUi = postToUi;
        _activity = activity;

        _connectionSubscription = host.EventBus.Subscribe<DeviceConnectionStateChanged>((e, _) =>
        {
            if (e.DeviceId == device.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });
        activity.Changed += OnActivityChanged;
    }

    /// <summary>The device itself, for panels that work with its capability interfaces.</summary>
    public IDevice DeviceModel => _device;

    /// <summary>Posts an action to the UI thread; for panels that belong to this device.</summary>
    public void PostToUiThread(Action action) => PostToUi(action);

    /// <summary>A sequence is running; manual operations are not offered meanwhile.</summary>
    public bool IsSessionBusy => IsSequenceRunning;

    /// <summary>Raised when what manual commands may do changed (a sequence started or ended, the connection changed).</summary>
    public event EventHandler? CommandsRefreshed;

    protected SideraRuntimeHost Host { get; }
    protected Action<Action> PostToUi { get; }
    protected StateStore StateStore => Host.StateStore;
    protected DeviceId Id => _device.Id;

    /// <summary>A sequence is running; manual operations are not offered meanwhile.</summary>
    protected bool IsSequenceRunning => _activity.IsSequenceRunning;

    public string Name => _device.Name;
    public string DeviceIdText => _device.Id.Value;
    public string Kind => _device.Type.ToString();

    /// <summary>The kind as a title: "Camera", "Filter Wheel", "Focuser".</summary>
    public string KindTitle => _device.Type switch
    {
        DeviceType.FilterWheel => "Filter Wheel",
        var type => type.ToString(),
    };

    /// <summary>
    /// What drives the device: "ASCOM" for a device that says so (<see cref="IBackendDescribed"/>), the simulator, which is
    /// recognised by the device class because the device interfaces do not say, or "Driver".
    /// </summary>
    public string BackendText => _device is IBackendDescribed described
        ? described.BackendName
        : _device.GetType().Name.StartsWith("Simulated", StringComparison.Ordinal) ? "Simulator" : "Driver";

    /// <summary>The identifier of the driver within its backend (the ProgId of an ASCOM driver); <c>null</c> when there is none.</summary>
    public string? DriverIdText => (_device as IBackendDescribed)?.DriverId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusKind))]
    public partial DeviceConnectionState ConnectionState { get; private set; }

    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

    public bool IsDisconnected => !IsConnected;

    /// <summary>The connection in one word: Connected, Disconnected, Connecting, Disconnecting, Faulted.</summary>
    public string StatusText => ConnectionState.ToString();

    public StatusKind StatusKind => ConnectionState switch
    {
        DeviceConnectionState.Connected => StatusKind.Ok,
        DeviceConnectionState.Connecting or DeviceConnectionState.Disconnecting => StatusKind.Active,
        DeviceConnectionState.Faulted => StatusKind.Error,
        _ => StatusKind.Neutral,
    };

    /// <summary>
    /// What the device is doing besides being connected, in one line ("Idle", "Exposing 40 %", "Moving", "Tracking");
    /// empty while it is not connected. Each device kind says what it knows.
    /// </summary>
    [ObservableProperty]
    public partial string ActivityText { get; private set; } = string.Empty;

    /// <summary>The activity has progress that can be drawn (an exposure), from 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityProgress))]
    public partial double? ActivityProgress { get; private set; }

    public bool HasActivityProgress => ActivityProgress is not null;

    /// <summary>The device is doing something: its activity is drawn in the accent.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Selects this device on the equipment page, which shows its detail. Set by the page that lists the devices;
    /// <c>null</c> where no page offers it.
    /// </summary>
    public ICommand? OpenCommand { get; set; }

    /// <summary>This is the device whose detail the equipment page shows; the browser marks its row.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync() => RunAsync(() => Host.DeviceOperations.ConnectAsync(Id));

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private Task DisconnectAsync() => RunAsync(() => Host.DeviceOperations.DisconnectAsync(Id));

    protected virtual bool CanConnect() => !IsSequenceRunning && ConnectionState == DeviceConnectionState.Disconnected;

    protected virtual bool CanDisconnect() => !IsSequenceRunning && IsConnected;

    /// <summary>Reads the device's state again (UI thread). Derived classes extend <see cref="RefreshDeviceState"/>.</summary>
    public void Refresh()
    {
        ConnectionState = StateStore.TryGet(Id, out var state) && state is not null
            ? state.ConnectionState
            : _device.ConnectionState;
        RefreshDeviceState();
        RefreshSummary();
        RefreshCommands();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads <see cref="ActivityText"/>, its progress and busyness again. Cheap; the camera calls it while it exposes.</summary>
    protected void RefreshSummary()
    {
        var activity = IsConnected ? DescribeActivity() : default;
        ActivityText = activity.Text ?? string.Empty;
        ActivityProgress = activity.Progress;
        IsBusy = activity.Busy;
    }

    /// <summary>What the device is doing now; the base knows nothing beyond the connection.</summary>
    protected virtual DeviceActivity DescribeActivity() => default;

    /// <summary>Raised on the UI thread each time the device state was read again.</summary>
    public event EventHandler? Refreshed;

    protected virtual void RefreshDeviceState()
    {
    }

    protected virtual void RefreshCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        CommandsRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a manual operation: clears the previous error, and shows a failure as a sentence instead of letting it escape.</summary>
    protected async Task RunAsync(Func<Task> operation)
    {
        ClearError();
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; nothing went wrong.
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            Refresh();
        }
    }

    private void OnActivityChanged(object? sender, EventArgs e) => RefreshCommands();

    public virtual void Dispose()
    {
        _activity.Changed -= OnActivityChanged;
        _connectionSubscription.Dispose();
    }
}
