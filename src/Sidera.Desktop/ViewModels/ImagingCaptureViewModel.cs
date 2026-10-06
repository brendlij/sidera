using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Imaging;
using Sidera.Runtime;

namespace Sidera.Desktop.ViewModels;

/// <summary>What a manual capture can be made with: a rig (its camera), or a camera that is in no rig.</summary>
public sealed record CaptureTarget(string Label, DeviceId CameraId, RigId? RigId);

/// <summary>A frame type in the capture bar: light, dark, flat or bias.</summary>
public sealed record FrameTypeChoice(string Text, FrameType Value);

/// <summary>A binning in the capture bar ("2 × 2") and the factor behind it.</summary>
public sealed record BinningChoice(string Text, int Value);

/// <summary>
/// Manual capture on the imaging page: the camera of the selected rig takes one frame through the same acquisition pipeline as a sequence exposure (the settings of the camera, the overrides
/// entered here, the checks against what the camera supports, the camera's resource), and the frame is shown. There is no other exposure path: nothing here talks to a driver. Only what the
/// camera supports is offered. Cancelling asks the exposure to stop, and no frame is shown for a cancelled exposure.
/// </summary>
public sealed partial class ImagingCaptureViewModel : ViewModelBase
{
    private readonly SideraRuntimeHost _host;
    private readonly ImagingViewModel _imaging;
    private CancellationTokenSource? _exposure;

    public ImagingCaptureViewModel(SideraRuntimeHost host, ImagingViewModel imaging, double defaultExposureSeconds = 5)
    {
        _host = host;
        _imaging = imaging;
        ExposureText = defaultExposureSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        FrameTypes = [.. Enum.GetValues<FrameType>().Select(t => new FrameTypeChoice(t.ToString(), t))];
        SelectedFrameType = FrameTypes[0];
        Refresh();
    }

    public ObservableCollection<CaptureTarget> Targets { get; } = [];

    public IReadOnlyList<FrameTypeChoice> FrameTypes { get; }

    /// <summary>The binnings the selected camera offers ("1 × 1", "2 × 2", ...); empty for a camera that cannot bin or is not connected.</summary>
    public ObservableCollection<BinningChoice> Binnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget), nameof(CameraName), nameof(DisabledText))]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    public partial CaptureTarget? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial string ExposureText { get; set; }

    [ObservableProperty]
    public partial FrameTypeChoice SelectedFrameType { get; set; }

    /// <summary>The gain to take the frame with; empty is the gain the camera has (its default).</summary>
    [ObservableProperty]
    public partial string GainText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OffsetText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial BinningChoice? SelectedBinning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(DisabledText))]
    public partial bool IsCapturing { get; private set; }

    /// <summary>What the capture is doing or did, in a sentence.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>The problem of the last capture, when it had one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = string.Empty;

    public new bool HasError => ErrorText.Length > 0;

    public bool HasTarget => SelectedTarget is not null;

    public string CameraName => Camera?.Name ?? "No camera";

    private ICamera? Camera => SelectedTarget is { } t && _host.DeviceRegistry.TryGet(t.CameraId, out var device) ? device as ICamera : null;

    private CameraCapabilities? Capabilities => (Camera as ICameraControl)?.Capabilities.Value;

    public bool IsConnected => Camera?.ConnectionState == DeviceConnectionState.Connected;

    /// <summary>The settings that this camera supports are offered; those it does not are not there.</summary>
    public bool ShowGain => Capabilities?.Gain is not null;

    public bool ShowOffset => Capabilities?.Offset is not null;

    public bool ShowBinning => Binnings.Count > 1;

    /// <summary>Why capturing is not possible now, in a sentence; empty when it is.</summary>
    public string DisabledText =>
        SelectedTarget is null ? "There is no camera. Add one on the Equipment page."
        : !IsConnected ? $"Connect {CameraName} on the Equipment page to capture."
        : IsCapturing ? "An exposure is running."
        : string.Empty;

    /// <summary>Reads the rigs and the cameras again (equipment came or went, a camera was connected) and what the selected camera supports.</summary>
    public void Refresh()
    {
        var previous = SelectedTarget?.CameraId;
        var inRigs = new HashSet<DeviceId>();
        var targets = new List<CaptureTarget>();
        foreach (var rig in _host.RigRegistry.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            inRigs.Add(rig.CameraId);
            targets.Add(new CaptureTarget(rig.Name, rig.CameraId, rig.Id));
        }

        foreach (var camera in _host.DeviceRegistry.GetAll().OfType<ICamera>().Where(c => !inRigs.Contains(c.Id)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            targets.Add(new CaptureTarget(camera.Name + " (no rig)", camera.Id, null));
        }

        if (!targets.SequenceEqual(Targets))
        {
            Targets.Clear();
            foreach (var target in targets)
            {
                Targets.Add(target);
            }
        }

        SelectedTarget = Targets.FirstOrDefault(t => t.CameraId == previous) ?? Targets.FirstOrDefault();
        RefreshCapabilities();
    }

    partial void OnSelectedTargetChanged(CaptureTarget? value) => RefreshCapabilities();

    /// <summary>Reads what the selected camera supports (it can change when the camera is connected or disconnected).</summary>
    public void RefreshCapabilities()
    {
        var max = Capabilities?.MaxBinX ?? 1;
        var supported = Capabilities is { SupportsBinning: true } ? Enumerable.Range(1, Math.Min(max, 4)).ToList() : [];
        if (!supported.Select(b => b).SequenceEqual(Binnings.Select(b => b.Value)))
        {
            Binnings.Clear();
            foreach (var bin in supported)
            {
                Binnings.Add(new BinningChoice($"{bin} × {bin}", bin));
            }

            SelectedBinning = Binnings.FirstOrDefault();
        }

        OnPropertyChanged(nameof(ShowGain));
        OnPropertyChanged(nameof(ShowOffset));
        OnPropertyChanged(nameof(ShowBinning));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(DisabledText));
        CaptureCommand.NotifyCanExecuteChanged();
    }

    private bool CanCapture() => SelectedTarget is not null && IsConnected && !IsCapturing;

    /// <summary>
    /// The acquisition that the entries ask for: the frame type, and only the settings that were entered or chosen differently from the camera (the rest is the camera's own, as everywhere).
    /// Reports what is wrong with an entry instead of guessing.
    /// </summary>
    public bool TryBuildIntent(out AcquisitionIntent intent, out TimeSpan duration, out string? problem)
    {
        intent = AcquisitionIntent.Default;
        duration = TimeSpan.Zero;
        problem = null;
        if (!TryNumber(ExposureText, out var seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            problem = "Exposure must be a number of seconds greater than 0.";
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        intent = new AcquisitionIntent { FrameType = SelectedFrameType.Value };
        if (ShowGain && GainText.Trim().Length > 0)
        {
            if (!int.TryParse(GainText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gain))
            {
                problem = "Gain must be a whole number, or empty for the camera's own.";
                return false;
            }

            intent = intent with { Gain = AcquisitionLevel.OfNumber(gain) };
        }

        if (ShowOffset && OffsetText.Trim().Length > 0)
        {
            if (!int.TryParse(OffsetText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                problem = "Offset must be a whole number, or empty for the camera's own.";
                return false;
            }

            intent = intent with { Offset = AcquisitionLevel.OfNumber(offset) };
        }

        if (ShowBinning && SelectedBinning is { } binning && binning.Value > 1)
        {
            intent = intent with { BinX = binning.Value, BinY = binning.Value };
        }

        return true;
    }

    private static bool TryNumber(string text, out double value) =>
        (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        && double.IsFinite(value);

    /// <summary>Takes one frame with the camera of the selected rig and shows it on this page.</summary>
    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        if (SelectedTarget is not { } target)
        {
            return;
        }

        ErrorText = string.Empty;
        if (!TryBuildIntent(out var intent, out var duration, out var problem))
        {
            ErrorText = problem!;
            return;
        }

        var source = new CancellationTokenSource();
        _exposure = source;
        IsCapturing = true;
        StatusText = string.Create(CultureInfo.InvariantCulture, $"Exposing {duration.TotalSeconds:0.###} s…");
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var frame = await _host.DeviceOperations.ExposeAsync(target.CameraId, duration, intent, source.Token);
            var capture = FrameExporter.CaptureContext(_host, target.CameraId, startedAt);
            _imaging.Publish(frame, $"{target.Label} · {CameraName} (manual)", target.CameraId, capture);
            if (_imaging.FitOnCapture)
            {
                _imaging.FitCommand.Execute(null);
            }
            StatusText = string.Create(CultureInfo.InvariantCulture, $"{frame.ExposureDuration.TotalSeconds:0.###} s {intent.FrameType.ToString().ToLowerInvariant()} frame, {frame.Width} × {frame.Height}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = string.Empty;
        }
        finally
        {
            _exposure = null;
            source.Dispose();
            IsCapturing = false;
            RefreshCapabilities();
        }
    }

    [RelayCommand(CanExecute = nameof(IsCapturing))]
    private void Cancel()
    {
        try
        {
            _exposure?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The exposure just ended.
        }
    }
}
