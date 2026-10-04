using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Imaging;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The imaging page shows metrics only of a real analysis of the frame it shows.</summary>
public class ImagingMetricsTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static CameraFrame Sky(double sigma, int exposure = 1, SimulatedSkyOptions? options = null) =>
        new SimulatedSky(1, options).Render(sigma, TimeSpan.FromSeconds(1), exposure);

    [Fact]
    public void WithoutAnAnalyzer_ThePageShowsTheFrameOnly()
    {
        var imaging = new ImagingViewModel();

        imaging.Publish(Sky(2), "manual");

        Assert.False(imaging.CanAnalyze);
        Assert.False(imaging.HasMetrics);
        Assert.Equal(FrameAnalysisState.None, imaging.AnalysisState);
        Assert.Empty(imaging.Stars);
    }

    [Fact]
    public void BeforeAnyFrame_ThereAreNoMetrics()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());

        Assert.False(imaging.HasMetrics);
        Assert.Null(imaging.Metrics);
        Assert.False(imaging.IsAnalyzing);
        Assert.Equal(string.Empty, imaging.StarsText);
    }

    [Fact]
    public async Task AFrame_GetsItsMetrics_FromItsPixels()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());

        imaging.Publish(Sky(2.0), "manual");
        await imaging.AnalysisCompletion;

        Assert.Equal(FrameAnalysisState.Done, imaging.AnalysisState);
        Assert.True(imaging.HasMetrics);
        Assert.True(imaging.Metrics!.UsableStarCount >= 15);
        Assert.InRange(imaging.Metrics.MedianHfr!.Value, 2.2, 2.6); // sigma 2.0 is an HFR of 2.355
        Assert.Matches(@"^\d+ usable \(\d+ found\)$", imaging.StarsText);
        Assert.Matches(@"^2\.\d\d px$", imaging.MedianHfrText);
        Assert.Matches(@"^\d+ ADU$", imaging.BackgroundText);
        Assert.Matches(@"^\d+\.\d ADU$", imaging.NoiseText);
        Assert.Equal("none", imaging.SaturatedText);
        Assert.Equal(imaging.Metrics.UsableStarCount + imaging.Stars.Count(s => !s.IsUsable), imaging.Stars.Count);
        Assert.Null(imaging.AnalysisMessage);
    }

    [Fact]
    public async Task ABlurredFrame_ShowsAHigherHfr_ThanAFocusedOne()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());

        imaging.Publish(Sky(1.8), "focused");
        await imaging.AnalysisCompletion;
        var focused = imaging.Metrics!.MedianHfr!.Value;
        imaging.Publish(Sky(4.5), "blurred");
        await imaging.AnalysisCompletion;

        Assert.True(imaging.Metrics!.MedianHfr!.Value > focused * 2);
    }

    [Fact]
    public async Task ANewFrame_ClearsTheMetricsOfTheOldOne_UntilItsOwnAnalysisIsDone()
    {
        var analyzer = new GatedAnalyzer(new FrameAnalyzer());
        var imaging = new ImagingViewModel(analyzer, a => a());
        imaging.Publish(Sky(2.0), "first");
        analyzer.Release();
        await imaging.AnalysisCompletion;
        Assert.True(imaging.HasMetrics);

        imaging.Publish(Sky(3.0, exposure: 2), "second"); // the analyzer waits for the gate

        Assert.True(imaging.IsAnalyzing);
        Assert.False(imaging.HasMetrics);
        Assert.Empty(imaging.Stars);
        analyzer.Release();
        await imaging.AnalysisCompletion;
        Assert.False(imaging.IsAnalyzing);
        Assert.True(imaging.HasMetrics);
    }

    [Fact]
    public async Task TheResultOfASupersededFrame_IsNeverShown_AndItsAnalysisIsCancelled()
    {
        var analyzer = new GatedAnalyzer(new FrameAnalyzer());
        var imaging = new ImagingViewModel(analyzer, a => a());

        imaging.Publish(Sky(1.8), "old");
        var oldRun = imaging.AnalysisCompletion;
        await analyzer.Started(1);
        imaging.Publish(Sky(5.0, exposure: 2), "new");
        await oldRun.WaitAsync(Bound); // ends by its cancellation: the gate was never opened for it
        analyzer.Release();
        await imaging.AnalysisCompletion.WaitAsync(Bound);

        Assert.True(analyzer.WasCancelled(0));
        Assert.Equal("new", imaging.SourceText);
        Assert.InRange(imaging.Metrics!.MedianHfr!.Value, 5.0 * 1.1774 * 0.9, 5.0 * 1.1774 * 1.1); // the metrics of the new frame
    }

    [Fact]
    public async Task AFrameWithoutStars_SaysSo_InsteadOfShowingNumbers()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());

        imaging.Publish(Sky(2.0, options: new SimulatedSkyOptions(StarCount: 0)), "empty");
        await imaging.AnalysisCompletion;

        Assert.Equal(FrameAnalysisState.Done, imaging.AnalysisState);
        Assert.Equal("No stars were detected in this frame.", imaging.AnalysisMessage);
        Assert.Equal("–", imaging.MedianHfrText);
        Assert.Equal(0, imaging.Metrics!.UsableStarCount);
    }

    [Fact]
    public async Task AFrameWithOnlySaturatedStars_HasNoUsableStars_AndSaysWhy()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());

        imaging.Publish(Sky(2.0, options: new SimulatedSkyOptions(StarCount: 0, SaturatedStars: 6)), "saturated");
        await imaging.AnalysisCompletion;

        Assert.Equal(0, imaging.Metrics!.UsableStarCount);
        Assert.True(imaging.Metrics.SaturatedStarCount >= 5);
        Assert.Contains("No usable stars", imaging.AnalysisMessage);
        Assert.Contains("left out", imaging.SaturatedText);
    }

    [Fact]
    public async Task AFailingAnalysis_IsShownAsAnError_AndLeavesNoMetrics()
    {
        var imaging = new ImagingViewModel(new FailingAnalyzer(), a => a());

        imaging.Publish(Sky(2.0), "broken");
        await imaging.AnalysisCompletion;

        Assert.Equal(FrameAnalysisState.Failed, imaging.AnalysisState);
        Assert.False(imaging.HasMetrics);
        Assert.Equal("Frame analysis failed: boom", imaging.AnalysisMessage);
        Assert.True(imaging.HasAnalysisMessage);
    }

    [Fact]
    public async Task TheAnalysis_DoesNotRunOnTheThreadThatPublishes_AndTheResultComesBackThroughThePost()
    {
        var posted = new List<int>();
        var publisher = Environment.CurrentManagedThreadId;
        var analyzer = new ThreadRecordingAnalyzer(new FrameAnalyzer());
        var imaging = new ImagingViewModel(analyzer, action => { posted.Add(Environment.CurrentManagedThreadId); action(); });

        imaging.Publish(Sky(2.0), "manual");
        await imaging.AnalysisCompletion;

        Assert.NotEqual(publisher, analyzer.ThreadId);
        Assert.Single(posted);
        Assert.True(imaging.HasMetrics);
    }

    [Fact]
    public async Task TheStarOverlay_IsOffByDefault_AndTheStarsAreThereForItWhenSwitchedOn()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), a => a());
        imaging.Publish(Sky(2.0), "manual");
        await imaging.AnalysisCompletion;

        Assert.False(imaging.ShowStars);
        Assert.NotEmpty(imaging.Stars);
        Assert.All(imaging.Stars, s => Assert.InRange(s.X, 0, 800));
    }

    [Fact]
    public async Task AFrameIsAnalysedOnlyOnce_ForThePageAndForAutofocus()
    {
        var counting = new GatedAnalyzer(new FrameAnalyzer());
        counting.Release(10);
        var imaging = new ImagingViewModel(counting, a => a());
        var frame = Sky(2.0);

        imaging.Publish(frame, "manual");
        await imaging.AnalysisCompletion;
        imaging.Publish(frame, "again");
        await imaging.AnalysisCompletion;

        Assert.Equal(2, counting.Calls); // the page asks twice; the shared analyzer behind it computes once
        Assert.Same(counting.Results[0], counting.Results[1]);
    }

    private sealed class GatedAnalyzer(IFrameAnalyzer inner) : IFrameAnalyzer
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly List<TaskCompletionSource> _started = [];
        private readonly List<bool> _cancelled = [];
        private int _calls;

        public FrameAnalysisOptions Options => inner.Options;
        public int Calls => Volatile.Read(ref _calls);
        public List<FrameAnalysisResult> Results { get; } = [];

        public void Release(int count = 1) => _gate.Release(count);

        public Task Started(int count)
        {
            lock (_started)
            {
                while (_started.Count < count)
                {
                    _started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _started[count - 1].Task;
            }
        }

        public bool WasCancelled(int call)
        {
            lock (_cancelled)
            {
                return _cancelled[call];
            }
        }

        public FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default)
        {
            int index;
            lock (_started)
            {
                index = _calls++;
                while (_started.Count <= index)
                {
                    _started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                _started[index].TrySetResult();
            }

            lock (_cancelled)
            {
                while (_cancelled.Count <= index)
                {
                    _cancelled.Add(false);
                }
            }

            try
            {
                _gate.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lock (_cancelled)
                {
                    _cancelled[index] = true;
                }

                throw;
            }

            var result = inner.Analyze(frame, cancellationToken);
            lock (Results)
            {
                Results.Add(result);
            }

            return result;
        }
    }

    private sealed class FailingAnalyzer : IFrameAnalyzer
    {
        public FrameAnalysisOptions Options { get; } = new();

        public FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ThreadRecordingAnalyzer(IFrameAnalyzer inner) : IFrameAnalyzer
    {
        public int ThreadId { get; private set; }
        public FrameAnalysisOptions Options => inner.Options;

        public FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return inner.Analyze(frame, cancellationToken);
        }
    }
}
