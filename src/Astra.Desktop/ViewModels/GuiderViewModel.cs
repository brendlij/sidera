using System;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// A guider card: connection, guiding state, start and stop. Dithering is not a manual operation: it moves the
/// mount and disturbs the cameras, so it only exists inside a sequence, where it is coordinated with the other
/// branches. The card only says whether the guider is capable of it.
/// </summary>
public sealed partial class GuiderViewModel : DeviceViewModelBase
{
    private readonly IGuider _guider;
    private readonly IDisposable _guidingSubscription;

    public GuiderViewModel(IGuider guider, AstraRuntimeHost host, Action<Action> postToUi, SessionActivity activity)
        : base(guider, host, postToUi, activity)
    {
        _guider = guider;
        _guidingSubscription = host.EventBus.Subscribe<GuidingStateChanged>((e, _) =>
        {
            if (e.DeviceId == guider.Id)
            {
                postToUi(Refresh);
            }

            return Task.CompletedTask;
        });

        Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuiding))]
    public partial GuidingState GuidingState { get; private set; }

    public bool IsGuiding => GuidingState == GuidingState.Guiding;

    /// <summary>The guider can dither (when a sequence asks for it).</summary>
    public bool SupportsDither => _guider is IDitherGuider;

    /// <summary>The guider can report when guiding has settled after a dither.</summary>
    public bool SupportsSettle => _guider is IGuidingSettler;

    public string DitherSupportText => SupportsDither ? "Supported" : "Not supported";

    public string SettleSupportText => SupportsSettle ? "Supported" : "Not supported";

    [RelayCommand(CanExecute = nameof(CanStartGuiding))]
    private Task StartGuidingAsync() => RunAsync(() => Host.DeviceOperations.StartGuidingAsync(Id));

    [RelayCommand(CanExecute = nameof(CanStopGuiding))]
    private Task StopGuidingAsync() => RunAsync(() => Host.DeviceOperations.StopGuidingAsync(Id));

    protected override bool CanDisconnect() =>
        base.CanDisconnect() && GuidingState is GuidingState.Idle or GuidingState.Guiding;

    private bool CanStartGuiding() => !IsSequenceRunning && IsConnected && GuidingState == GuidingState.Idle;

    private bool CanStopGuiding() => !IsSequenceRunning && IsConnected && GuidingState == GuidingState.Guiding;

    protected override void RefreshDeviceState()
    {
        GuidingState = StateStore.TryGet(Id, out var state) && state?.GuidingState is { } guiding
            ? guiding
            : _guider.GuidingState;
    }

    protected override void RefreshCommands()
    {
        base.RefreshCommands();
        StartGuidingCommand.NotifyCanExecuteChanged();
        StopGuidingCommand.NotifyCanExecuteChanged();
    }

    protected override DeviceActivity DescribeActivity() => GuidingState switch
    {
        GuidingState.Guiding => new DeviceActivity("Guiding"),
        GuidingState.Dithering => new DeviceActivity("Dithering", null, true),
        GuidingState.Starting => new DeviceActivity("Starting guiding", null, true),
        GuidingState.Stopping => new DeviceActivity("Stopping guiding", null, true),
        _ => new DeviceActivity("Idle"),
    };

    public override void Dispose()
    {
        _guidingSubscription.Dispose();
        base.Dispose();
    }
}
