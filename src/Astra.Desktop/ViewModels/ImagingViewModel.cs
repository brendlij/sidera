using System;
using System.Globalization;
using Astra.Core.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The latest frame, whichever way it was taken (manual exposure or sequence). Only what a frame really carries is
/// shown: size and exposure time. There is no histogram, no star analysis and no saving.
/// </summary>
public sealed partial class ImagingViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrame))]
    [NotifyPropertyChangedFor(nameof(DimensionsText))]
    [NotifyPropertyChangedFor(nameof(ExposureText))]
    public partial CameraFrame? LatestFrame { get; private set; }

    /// <summary>Where the frame came from, for example "Main Camera (manual)" or "Demo sequence".</summary>
    [ObservableProperty]
    public partial string? SourceText { get; private set; }

    [ObservableProperty]
    public partial string? CapturedText { get; private set; }

    /// <summary>Number of frames received since the application started.</summary>
    [ObservableProperty]
    public partial int FrameCount { get; private set; }

    public bool HasFrame => LatestFrame is not null;

    public string DimensionsText => LatestFrame is { } f ? $"{f.Width} × {f.Height} px" : string.Empty;

    public string ExposureText => LatestFrame is { } f
        ? string.Create(CultureInfo.InvariantCulture, $"{f.ExposureDuration.TotalSeconds:0.##} s")
        : string.Empty;

    /// <summary>Makes <paramref name="frame"/> the latest one. Call on the UI thread.</summary>
    public void Publish(CameraFrame frame, string source)
    {
        ArgumentNullException.ThrowIfNull(frame);

        LatestFrame = frame;
        SourceText = source;
        CapturedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        FrameCount++;
    }
}
