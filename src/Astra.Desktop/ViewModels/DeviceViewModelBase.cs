using System;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Runtime;
using Astra.Runtime.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// What every device card shows and does: identity, connection state, connect and disconnect. State is read from
/// the <see cref="StateStore"/> after the event bus reports a change; changes arrive on any thread and are posted to
/// the UI thread. Connect and disconnect go through <c>DeviceOperationService</c>, so the runtime stays the boundary
/// that keeps conflicting operations apart; disabled buttons are only a courtesy.
/// </summary>
public abstract partial class DeviceViewModelBase : ViewModelBase, IDisposable
{
    private readonly IDevice _device;
    private readonly SessionActivity _activity;
    private readonly IDisposable _connectionSubscription;

    protected DeviceViewModelBase(
        IDevice device,
        AstraRuntimeHost host,
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

    protected AstraRuntimeHost Host { get; }
    protected Action<Action> PostToUi { get; }
    protected StateStore StateStore => Host.StateStore;
    protected DeviceId Id => _device.Id;

    /// <summary>A sequence is running; manual operations are not offered meanwhile.</summary>
    protected bool IsSequenceRunning => _activity.IsSequenceRunning;

    public string Name => _device.Name;
    public string DeviceIdText => _device.Id.Value;
    public string Kind => _device.Type.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    public partial DeviceConnectionState ConnectionState { get; private set; }

    public bool IsConnected => ConnectionState == DeviceConnectionState.Connected;

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
        RefreshCommands();
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread each time the device state was read again.</summary>
    public event EventHandler? Refreshed;

    protected virtual void RefreshDeviceState()
    {
    }

    protected virtual void RefreshCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
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
