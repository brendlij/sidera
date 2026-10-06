using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The latest frame, whichever way it was taken (manual exposure or sequence), and, when an analyzer is given, what the
/// analysis of its pixels found: usable stars, median HFR, background and noise. Nothing is shown that was not measured
/// from the frame. There is no histogram and no saving.
/// <para>
/// The analysis runs off the UI thread and is dropped when a newer frame arrives; until it is done the page shows that
/// it is working, never the numbers of the frame before.
/// </para>
/// </summary>
public sealed partial class ImagingViewModel : ViewModelBase, IDisposable
{
    private readonly IFrameAnalyzer? _analyzer;
    private readonly Action<Action> _postToUi;
    private CancellationTokenSource? _analysis;

    /// <param name="analyzer">The analysis of frames; without one the page shows the frame only.</param>
    /// <param name="postToUi">Runs an action on the UI thread; the analysis result is delivered through it.</param>
    public ImagingViewModel(IFrameAnalyzer? analyzer = null, Action<Action>? postToUi = null)
    {
        _analyzer = analyzer;
        _postToUi = postToUi ?? (action => action());
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrame))]
    [NotifyPropertyChangedFor(nameof(DimensionsText))]
    [NotifyPropertyChangedFor(nameof(ExposureText))]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(FrameWidth))]
    [NotifyPropertyChangedFor(nameof(FrameHeight))]
    [NotifyCanExecuteChangedFor(nameof(SaveFitsCommand), nameof(SavePngCommand))]
    public partial CameraFrame? LatestFrame { get; private set; }

    /// <summary>Where the frame came from, for example "Main Camera (manual)" or "Demo sequence".</summary>
    [ObservableProperty]
    public partial string? SourceText { get; private set; }

    [ObservableProperty]
    public partial string? CapturedText { get; private set; }

    /// <summary>Number of frames received since the application started.</summary>
    [ObservableProperty]
    public partial int FrameCount { get; private set; }

    /// <summary>Where the analysis of the latest frame stands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalyzing))]
    [NotifyPropertyChangedFor(nameof(AnalysisSummaryText))]
    public partial FrameAnalysisState AnalysisState { get; private set; }

    /// <summary>The metrics of the latest frame; <c>null</c> until its analysis is done, and when it failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetrics))]
    [NotifyPropertyChangedFor(nameof(StarsText))]
    [NotifyPropertyChangedFor(nameof(MedianHfrText))]
    [NotifyPropertyChangedFor(nameof(BackgroundText))]
    [NotifyPropertyChangedFor(nameof(NoiseText))]
    [NotifyPropertyChangedFor(nameof(SaturatedText))]
    [NotifyPropertyChangedFor(nameof(AnalysisSummaryText))]
    [NotifyPropertyChangedFor(nameof(HasUsableStars))]
    public partial FrameMetrics? Metrics { get; private set; }

    /// <summary>Why the analysis of the latest frame gave nothing (no stars, a failure), or <c>null</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysisMessage))]
    [NotifyPropertyChangedFor(nameof(AnalysisSummaryText))]
    public partial string? AnalysisMessage { get; private set; }

    /// <summary>The stars of the latest frame, for the overlay; empty until the analysis is done.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetectedStar> Stars { get; private set; } = [];

    /// <summary>Whether the stars are drawn over the frame.</summary>
    [ObservableProperty]
    public partial bool ShowStars { get; set; }

    /// <summary>The frame is shown pixel for pixel (1:1), scrolled when it is bigger than the view; otherwise it is fitted to the view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitToView))]
    [NotifyPropertyChangedFor(nameof(DisplayWidth))]
    [NotifyPropertyChangedFor(nameof(DisplayHeight))]
    [NotifyPropertyChangedFor(nameof(ScrollVisibility))]
    public partial bool IsActualSize { get; set; }

    /// <summary>The user zoomed or panned freely: neither Fit nor 1:1 describes the view. Set by the viewer; Fit and 1:1 clear it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitToView))]
    public partial bool IsCustomView { get; set; }

    /// <summary>The zoom of the viewer as a percentage; written by the viewer.</summary>
    [ObservableProperty]
    public partial string ZoomText { get; set; } = string.Empty;

    /// <summary>Show the frame with the automatic stretch (default) or linear. Only what is shown changes: the frame and the file that is saved do not.</summary>
    [ObservableProperty]
    public partial bool AutoStretch { get; set; } = true;

    public bool IsFitToView => !IsActualSize && !IsCustomView;

    /// <summary>The width of the latest frame in pixels, 0 without one.</summary>
    public double FrameWidth => LatestFrame?.Width ?? 0;

    public double FrameHeight => LatestFrame?.Height ?? 0;

    /// <summary>The size the frame is laid out at: its own size at 1:1, <c>NaN</c> (as much as there is) when fitted.</summary>
    public double DisplayWidth => IsActualSize && LatestFrame is { } frame ? frame.Width : double.NaN;

    public double DisplayHeight => IsActualSize && LatestFrame is { } frame ? frame.Height : double.NaN;

    /// <summary>Fitted frames never scroll; a frame at 1:1 does when it is bigger than the view.</summary>
    public Avalonia.Controls.Primitives.ScrollBarVisibility ScrollVisibility => IsActualSize
        ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

    [RelayCommand]
    private void Fit()
    {
        IsActualSize = false;
        IsCustomView = false;
    }

    [RelayCommand]
    private void ActualSize()
    {
        IsActualSize = true;
        IsCustomView = false;
    }

    /// <summary>The analysis of the latest frame; completed when it is done (or was dropped). For callers that wait.</summary>
    public Task AnalysisCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Manual capture with the camera of a rig, shown on this page; set by the application (a page without a host has none).</summary>
    public ImagingCaptureViewModel? Capture { get; set; }

    /// <summary>Starting the autofocus of a rig by hand; set by the application.</summary>
    public ManualAutofocusViewModel? Autofocus { get; set; }

    public bool HasCapture => Capture is not null;

    public bool HasAutofocus => Autofocus is not null;

    // ---- Saving what is shown

    /// <summary>Asks the person where to save: the suggested file name and the kind ("fits" or "png") in, the path out, <c>null</c> when they cancel. Set by the view.</summary>
    public Func<string, string, Task<string?>>? PickSavePath { get; set; }

    /// <summary>The folder the Save dialogs open in (a setting); <c>null</c> for the folder the dialog chooses.</summary>
    public Func<string?>? SaveDirectory { get; set; }

    /// <summary>A frame that a manual capture made is shown fitted to the view (a setting); off keeps the zoom and position that were chosen.</summary>
    public bool FitOnCapture { get; set; } = true;

    /// <summary>What the host knows about the equipment and the site, for the header of a FITS file. Set by the application.</summary>
    public Sidera.Runtime.SideraRuntimeHost? ExportHost { get; set; }

    public Func<Sidera.Core.Location.ObservingSite?>? ExportSite { get; set; }

    /// <summary>What the last save did, in a sentence; empty before one.</summary>
    [ObservableProperty]
    public partial string SaveStatusText { get; private set; } = string.Empty;

    /// <summary>Saves the frame as it was taken (never stretched) with what is known about how it was taken.</summary>
    [RelayCommand(CanExecute = nameof(HasFrame))]
    private async Task SaveFitsAsync()
    {
        if (LatestFrame is not { } frame || PickSavePath is null)
        {
            return;
        }

        var path = await PickSavePath(Imaging.FrameExporter.SuggestName(frame, "fits"), "fits");
        if (path is not null)
        {
            SaveFits(path);
        }
    }

    /// <summary>Saves the picture as it is shown, stretched or linear; the name of the file says which.</summary>
    [RelayCommand(CanExecute = nameof(HasFrame))]
    private async Task SavePngAsync()
    {
        if (LatestFrame is not { } frame || PickSavePath is null)
        {
            return;
        }

        var path = await PickSavePath(Imaging.FrameExporter.SuggestName(frame, "png", autoStretch: AutoStretch), "png");
        if (path is not null)
        {
            SavePng(path);
        }
    }

    /// <summary>Writes the latest frame as FITS to the path; the outcome is in <see cref="SaveStatusText"/>.</summary>
    public bool SaveFits(string path)
    {
        if (LatestFrame is not { } frame)
        {
            SaveStatusText = "There is no frame to save.";
            return false;
        }

        try
        {
            var metadata = Imaging.FrameExporter.MetadataFor(frame, ExportHost, LatestCameraId, ExportSite?.Invoke(), LatestCapture);
            Imaging.FrameExporter.SaveFits(frame, path, metadata);
            SaveStatusText = $"Saved the frame as FITS (the data as it was taken, not stretched): {System.IO.Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            SaveStatusText = "The frame could not be saved: " + ex.Message;
            return false;
        }
    }

    /// <summary>Writes the picture as it is shown (see <see cref="AutoStretch"/>) as PNG to the path.</summary>
    public bool SavePng(string path)
    {
        if (LatestFrame is not { } frame)
        {
            SaveStatusText = "There is no frame to save.";
            return false;
        }

        try
        {
            Imaging.FrameExporter.SavePng(frame, path, AutoStretch);
            SaveStatusText = $"Saved the picture as PNG, {(AutoStretch ? "auto stretched" : "linear")} as shown (not the data): {System.IO.Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            SaveStatusText = "The picture could not be saved: " + ex.Message;
            return false;
        }
    }

    public bool CanAnalyze => _analyzer is not null;

    public bool HasMetrics => Metrics is not null;

    /// <summary>The frame has stars its focus can be judged by: the median HFR is a number.</summary>
    public bool HasUsableStars => Metrics?.MedianHfr is not null;

    /// <summary>
    /// The analysis of the latest frame in one line, for the dashboard: "42 stars · HFR 1.79 px", or why there is none
    /// ("Analysing frame…", "No stars were detected in this frame."). Empty when frames are not analysed.
    /// </summary>
    public string AnalysisSummaryText => Metrics is { } m
        ? m.MedianHfr is { } hfr
            ? string.Create(CultureInfo.InvariantCulture, $"{m.UsableStarCount} stars · HFR {hfr:0.00} px")
            : AnalysisMessage ?? "No usable stars"
        : IsAnalyzing ? "Analysing frame…" : AnalysisMessage ?? string.Empty;

    public bool IsAnalyzing => AnalysisState == FrameAnalysisState.Analyzing;

    public bool HasAnalysisMessage => !string.IsNullOrEmpty(AnalysisMessage);

    public string StarsText => Metrics is { } m
        ? string.Create(CultureInfo.InvariantCulture, $"{m.UsableStarCount} usable ({m.StarCount} found)")
        : string.Empty;

    public string MedianHfrText => Metrics?.MedianHfr is { } hfr
        ? string.Create(CultureInfo.InvariantCulture, $"{hfr:0.00} px")
        : "–";

    public string BackgroundText => Metrics is { } m
        ? string.Create(CultureInfo.InvariantCulture, $"{m.Background:0} ADU")
        : string.Empty;

    public string NoiseText => Metrics is { } m
        ? string.Create(CultureInfo.InvariantCulture, $"{m.BackgroundSigma:0.0} ADU")
        : string.Empty;

    public string SaturatedText => Metrics is { } m
        ? m.SaturatedStarCount == 0
            ? "none"
            : string.Create(CultureInfo.InvariantCulture, $"{m.SaturatedStarCount} (left out)")
        : string.Empty;

    public bool HasFrame => LatestFrame is not null;

    public string DimensionsText => LatestFrame is { } f ? $"{f.Width} × {f.Height} px" : string.Empty;

    public string ExposureText => LatestFrame is { } f
        ? string.Create(CultureInfo.InvariantCulture, $"{f.ExposureDuration.TotalSeconds:0.##} s")
        : string.Empty;

    /// <summary>Makes <paramref name="frame"/> the latest one. Call on the UI thread.</summary>
    public DeviceId? LatestCameraId { get; private set; }

    /// <summary>What was known when the latest frame was taken and could not be asked for later (where the mount pointed): kept with the frame for the file that is saved.</summary>
    public Sidera.Core.Imaging.FitsMetadata? LatestCapture { get; private set; }

    public void Publish(CameraFrame frame, string source, DeviceId? cameraId = null, Sidera.Core.Imaging.FitsMetadata? capture = null)
    {
        ArgumentNullException.ThrowIfNull(frame);

        LatestFrame = frame;
        LatestCameraId = cameraId;
        LatestCapture = capture;
        SourceText = source;
        CapturedText = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        FrameCount++;
        StartAnalysis(frame);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _analysis, null)?.Cancel();
    }

    private void StartAnalysis(CameraFrame frame)
    {
        // Whatever was being analysed belongs to an older frame; its numbers must not appear next to this one.
        _analysis?.Cancel();
        Metrics = null;
        Stars = [];
        AnalysisMessage = null;

        if (_analyzer is null)
        {
            _analysis = null;
            AnalysisState = FrameAnalysisState.None;
            AnalysisCompletion = Task.CompletedTask;
            return;
        }

        var source = new CancellationTokenSource();
        _analysis = source;
        AnalysisState = FrameAnalysisState.Analyzing;
        AnalysisCompletion = Task.Run(() => Analyse(frame, source));
    }

    private void Analyse(CameraFrame frame, CancellationTokenSource source)
    {
        FrameAnalysisResult? result = null;
        string? failure = null;
        try
        {
            result = _analyzer!.Analyze(frame, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex is FrameAnalysisException ? ex.Message : $"Frame analysis failed: {ex.Message}";
        }

        _postToUi(() =>
        {
            // A newer frame has replaced this one while it was being analysed.
            if (!ReferenceEquals(_analysis, source) || source.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                AnalysisState = FrameAnalysisState.Failed;
                AnalysisMessage = failure;
                return;
            }

            Stars = result.Stars;
            Metrics = result.Metrics;
            AnalysisState = FrameAnalysisState.Done;
            AnalysisMessage = result.Metrics.StarCount == 0
                ? "No stars were detected in this frame."
                : result.Metrics.UsableStarCount == 0
                    ? "No usable stars (all saturated, clipped by the edge or elongated)."
                    : null;
        });
    }
}

public enum FrameAnalysisState
{
    /// <summary>No analysis: there is no frame, or no analyzer.</summary>
    None,
    Analyzing,
    Done,
    Failed,
}
