using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Sequencing;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The bar at the bottom of the window: what Sidera is doing right now, on every page. While a sequence runs it says which step (and, for every setup at work, what it is at and which frame); when nothing
/// runs it says so, why the sequence cannot start, or how the last run ended. It reads the sequencer and owns no state of its own.
/// </summary>
public sealed partial class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly SequencerViewModel _sequencer;
    private readonly ExecutionOverviewViewModel _execution;
    private readonly Action<Action> _postToUi;
    private Timer? _clock;

    public StatusBarViewModel(SequencerViewModel sequencer, ExecutionOverviewViewModel execution, Action<Action> postToUi)
    {
        _sequencer = sequencer;
        _execution = execution;
        _postToUi = postToUi;
        _sequencer.PropertyChanged += OnSequencerChanged;
        _execution.Refreshed += OnRefreshed;
        Refresh();
    }

    /// <summary>"Running", "Paused", "Idle", "Completed".</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusKind StateKind { get; private set; }

    /// <summary>"Step 3 / 5" for the top-level step that runs; empty with a single step and when nothing runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStepCounter))]
    public partial string StepCounterText { get; private set; } = string.Empty;

    public bool HasStepCounter => StepCounterText.Length > 0;

    /// <summary>What is going on, in one line: the step of each branch that runs, and what the session does that no track does (a dither).</summary>
    [ObservableProperty]
    public partial string ActivityText { get; private set; } = string.Empty;

    /// <summary>How long the run has gone on (or went on); empty before the first run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasElapsed))]
    public partial string ElapsedText { get; private set; } = string.Empty;

    public bool HasElapsed => ElapsedText.Length > 0;

    /// <summary>How long what Sidera is doing has been the same, once it has been so for a while: "for 1 min 32 s". It tells a step that takes long (a slew, a solve, a focus) from one that hangs; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStepAge))]
    public partial string StepAgeText { get; private set; } = string.Empty;

    public bool HasStepAge => StepAgeText.Length > 0;

    /// <summary>How long the line has to stay the same before its age is shown.</summary>
    public TimeSpan StepAgeAfter { get; set; } = TimeSpan.FromSeconds(20);

    private string _ageOf = string.Empty;
    private DateTime _ageSince = DateTime.UtcNow;

    /// <summary>The clock of the age of the step; tests move it.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private void OnSequencerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SequencerViewModel.State) or nameof(SequencerViewModel.ReadinessHint) or nameof(SequencerViewModel.StepText)
            or nameof(SequencerViewModel.SharedActivity) or nameof(SequencerViewModel.ActiveBranches) or nameof(SequencerViewModel.RunStartedAt) or nameof(SequencerViewModel.RunEndedAt))
        {
            Refresh();
        }
    }

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    /// <summary>Reads the sequencer again.</summary>
    public void Refresh()
    {
        StateText = _sequencer.StateText;
        StateKind = _sequencer.StateKind;
        StepCounterText = _sequencer.IsRunning ? _execution.StepCounterText : string.Empty;
        ActivityText = ReadActivity();
        RefreshElapsed();

        // The clock ticks only while a sequence runs.
        if (_sequencer.IsRunning && _clock is null)
        {
            _clock = new Timer(_ => _postToUi(RefreshElapsed), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else if (!_sequencer.IsRunning && _clock is not null)
        {
            _clock.Dispose();
            _clock = null;
        }
    }

    private string ReadActivity()
    {
        if (_sequencer.IsRunning)
        {
            var branches = _sequencer.ActiveBranches;
            var text = branches.Count == 0 ? _sequencer.StepText : string.Join("   ·   ", branches.Select(Describe));
            return _sequencer.SharedActivity is { Length: > 0 } shared ? (text.Length > 0 ? text + "   ·   " + shared : shared) : text;
        }

        return _sequencer.State switch
        {
            SequenceState.Failed => _sequencer.ErrorMessage ?? "The sequence failed.",
            SequenceState.Completed => "The sequence finished.",
            SequenceState.Cancelled => "The sequence was stopped.",
            _ => _sequencer.ReadinessHint ?? "Ready.",
        };
    }

    // "Main 750: Exposure 180 s · 6 / 40": the setup, the step that runs, and how far the innermost repetition has got. The way down through the containers is for the tree, not for this line.
    private static string Describe(ActiveBranchViewModel branch)
    {
        var line = (branch.HasBranchName ? branch.BranchName + ": " : string.Empty) + branch.DisplayTitle;
        var progress = Regex.Matches(branch.Context, @"Repeat × \d+ · (\d+ / \d+)");
        return progress.Count > 0 ? line + " · " + progress[^1].Groups[1].Value : line;
    }

    /// <summary>Reads the elapsed time of the run again; called every second while a run goes on.</summary>
    public void RefreshElapsed()
    {
        ElapsedText = _sequencer.Elapsed is { } elapsed ? Format(elapsed) : string.Empty;
        RefreshStepAge();
    }

    // The line that Sidera shows is the step and the frame it is at: when it has not changed for a while, how long it has not is said.
    private void RefreshStepAge()
    {
        var now = UtcNow();
        if (!string.Equals(_ageOf, ActivityText, StringComparison.Ordinal))
        {
            _ageOf = ActivityText;
            _ageSince = now;
        }

        var age = now - _ageSince;
        StepAgeText = _sequencer.IsRunning && ActivityText.Length > 0 && age >= StepAgeAfter
            ? "for " + TrackLaneViewModel.FormatDuration(age.TotalSeconds)
            : string.Empty;
    }

    private static string Format(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes}:{elapsed.Seconds:00}");

    public void Dispose()
    {
        _sequencer.PropertyChanged -= OnSequencerChanged;
        _execution.Refreshed -= OnRefreshed;
        _clock?.Dispose();
    }
}
