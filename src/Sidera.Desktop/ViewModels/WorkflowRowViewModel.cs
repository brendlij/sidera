using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Rigs;
using Sidera.Desktop.Workflows;

namespace Sidera.Desktop.ViewModels;

/// <summary>An imaging setup as a choice; <see cref="Id"/> is <c>null</c> for "Auto": the setup the workflow makes unambiguous.</summary>
public sealed record SetupChoice(RigId? Id, string Name, string Detail)
{
    public bool IsAuto => Id is null;
}

/// <summary>A filter of a setup's wheel as a choice; <see cref="Slot"/> is <c>null</c> for "no filter change".</summary>
public sealed record FilterChoice(int? Slot, string Name);

/// <summary>
/// One row of the workflow table: a step of Prepare or Finish, or a block of Imaging. It shows what the row says (the setup, a summary, the policies for an imaging block, and while running
/// its progress) and is what the inspector edits. Editing a field changes the row's model values at once and the workflow compiles again; a field that is not a number is told on the row and the
/// last good value stays.
/// </summary>
public sealed partial class WorkflowRowViewModel : ObservableObject
{
    private readonly WorkflowEditorViewModel _owner;
    private bool _loading;

    internal WorkflowRowViewModel(WorkflowEditorViewModel owner, WorkflowSection section, WorkflowStep? step, ImagingBlock? block)
    {
        _owner = owner;
        Section = section;
        Step = step;
        Block = block;
        Id = step?.Id ?? block!.Id;
        Load();
    }

    public Guid Id { get; }

    public WorkflowSection Section { get; }

    /// <summary>The model of a Prepare or Finish row; <c>null</c> for an imaging block.</summary>
    internal WorkflowStep? Step { get; private set; }

    /// <summary>The model of an imaging block; <c>null</c> for a Prepare or Finish row.</summary>
    internal ImagingBlock? Block { get; private set; }

    public bool IsImaging => Block is not null;

    public WorkflowStepKind? Kind => Step?.Kind;

    // What the inspector shows for which row.
    public bool ShowsSetup => true;
    public bool ShowsWait => Step is { Kind: WorkflowStepKind.Wait };
    public bool ShowsSlew => Step is { Kind: WorkflowStepKind.SlewAndCenter };
    public bool ShowsAutofocus => Step is { Kind: WorkflowStepKind.Autofocus };
    public bool ShowsGuiding => Step is { Kind: WorkflowStepKind.StartGuiding or WorkflowStepKind.StopGuiding };

    /// <summary>Position in its section, from 1.</summary>
    [ObservableProperty]
    public partial int Number { get; internal set; }

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    public string Title => Step?.Kind switch
    {
        WorkflowStepKind.SlewAndCenter => "Slew & Center",
        WorkflowStepKind.Autofocus => "Autofocus",
        WorkflowStepKind.StartGuiding => "Start Guiding",
        WorkflowStepKind.StopGuiding => "Stop Guiding",
        WorkflowStepKind.Wait => "Wait",
        _ => "Imaging",
    };

    // ---- what the table shows

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string SetupLabel { get; internal set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Summary { get; internal set; } = string.Empty;

    /// <summary>What a screen reader (and a UI test) calls the row: its title, setup and summary.</summary>
    public string AutomationName => $"{Title}, {SetupLabel}, {Summary}";

    [ObservableProperty]
    public partial string FilterLabel { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string ExposureLabel { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string FramesLabel { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherLabel { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial string AutofocusLabel { get; internal set; } = string.Empty;

    /// <summary>What is wrong with the row, in a sentence; empty when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; internal set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    // ---- progress while the workflow runs (the lane of the setup)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial string ProgressText { get; internal set; } = string.Empty;

    public bool HasProgress => ProgressText.Length > 0;

    [ObservableProperty]
    public partial double ProgressFraction { get; internal set; }

    /// <summary>What the setup is doing right now: the step it is at, or the autofocus it is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    public partial string ActivityText { get; internal set; } = string.Empty;

    public bool HasActivity => ActivityText.Length > 0;

    // ---- the fields of the inspector

    public ObservableCollection<SetupChoice> SetupChoices { get; } = [];

    public ObservableCollection<FilterChoice> FilterChoices { get; } = [];

    [ObservableProperty]
    public partial SetupChoice? SelectedSetup { get; set; }

    [ObservableProperty]
    public partial FilterChoice? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FramesInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SecondsInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ToleranceInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttemptsInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveExposureInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutofocusExposureInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutofocusStepInputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AutofocusSamplesInputText { get; set; } = string.Empty;

    /// <summary>Reads the values of the model into the fields (without that being an edit).</summary>
    internal void Load()
    {
        _loading = true;
        try
        {
            Enabled = Step?.Enabled ?? Block!.Enabled;
            if (Step is { } step)
            {
                SecondsInputText = Format(step.Seconds);
                ToleranceInputText = Format(step.ToleranceArcseconds);
                AttemptsInputText = step.MaxAttempts.ToString(CultureInfo.InvariantCulture);
                SolveExposureInputText = Format(step.SolveExposureSeconds);
                var autofocus = step.Autofocus ?? AutofocusSettings.Default;
                AutofocusExposureInputText = Format(autofocus.ExposureSeconds);
                AutofocusStepInputText = autofocus.StepSize.ToString(CultureInfo.InvariantCulture);
                AutofocusSamplesInputText = autofocus.SampleCount.ToString(CultureInfo.InvariantCulture);
            }

            if (Block is { } block)
            {
                ExposureText = Format(block.ExposureSeconds);
                FramesInputText = block.Frames.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    internal void SetChoices(IEnumerable<SetupChoice> setups, SetupChoice? selected)
    {
        _loading = true;
        try
        {
            SetupChoices.Clear();
            foreach (var choice in setups)
            {
                SetupChoices.Add(choice);
            }

            SelectedSetup = selected is null ? null : SetupChoices.FirstOrDefault(c => c.Id == selected.Id);
        }
        finally
        {
            _loading = false;
        }
    }

    internal void SetFilters(IEnumerable<FilterChoice> filters, int? slot)
    {
        _loading = true;
        try
        {
            FilterChoices.Clear();
            foreach (var choice in filters)
            {
                FilterChoices.Add(choice);
            }

            SelectedFilter = FilterChoices.FirstOrDefault(c => c.Slot == slot) ?? FilterChoices.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    // Every field of the inspector reads itself again when it changes; the editor compiles.
    partial void OnEnabledChanged(bool value) => Edited();
    partial void OnSelectedSetupChanged(SetupChoice? value) => Edited();
    partial void OnSelectedFilterChanged(FilterChoice? value) => Edited();
    partial void OnExposureTextChanged(string value) => Edited();
    partial void OnFramesInputTextChanged(string value) => Edited();
    partial void OnSecondsInputTextChanged(string value) => Edited();
    partial void OnToleranceInputTextChanged(string value) => Edited();
    partial void OnAttemptsInputTextChanged(string value) => Edited();
    partial void OnSolveExposureInputTextChanged(string value) => Edited();
    partial void OnAutofocusExposureInputTextChanged(string value) => Edited();
    partial void OnAutofocusStepInputTextChanged(string value) => Edited();
    partial void OnAutofocusSamplesInputTextChanged(string value) => Edited();

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        if (Step is { } step)
        {
            var seconds = ParseNumber(SecondsInputText, "The wait", "a number of seconds", problems, step.Seconds);
            var tolerance = ParseNumber(ToleranceInputText, "The tolerance", "a number of arcseconds", problems, step.ToleranceArcseconds);
            var attempts = ParseWhole(AttemptsInputText, "The attempts", problems, step.MaxAttempts);
            var solve = ParseNumber(SolveExposureInputText, "The plate solve exposure", "a number of seconds", problems, step.SolveExposureSeconds);
            var af = step.Autofocus ?? AutofocusSettings.Default;
            var afSettings = new AutofocusSettings(
                ParseNumber(AutofocusExposureInputText, "The autofocus exposure", "a number of seconds", problems, af.ExposureSeconds),
                ParseWhole(AutofocusStepInputText, "The autofocus step size", problems, af.StepSize),
                ParseWhole(AutofocusSamplesInputText, "The autofocus samples", problems, af.SampleCount));
            Step = step with
            {
                Setup = SelectedSetup?.Id, Enabled = Enabled, Seconds = seconds, ToleranceArcseconds = tolerance, MaxAttempts = attempts, SolveExposureSeconds = solve,
                Autofocus = step.Kind == WorkflowStepKind.Autofocus ? afSettings : step.Autofocus,
            };
        }
        else if (Block is { } block)
        {
            var exposure = ParseNumber(ExposureText, "The exposure", "a number of seconds", problems, block.ExposureSeconds);
            var frames = ParseWhole(FramesInputText, "The number of frames", problems, block.Frames);
            Block = block with { Setup = SelectedSetup?.Id, FilterSlot = SelectedFilter?.Slot, ExposureSeconds = exposure, Frames = frames, Enabled = Enabled };
        }

        ParseProblems = problems;
        _owner.RowEdited(this);
    }

    /// <summary>The fields of this row that do not read as numbers, as sentences.</summary>
    internal IReadOnlyList<string> ParseProblems { get; private set; } = [];

    internal void ReplaceModel(WorkflowStep? step, ImagingBlock? block)
    {
        Step = step ?? Step;
        Block = block ?? Block;
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static double ParseNumber(string? text, string label, string expected, List<string> problems, double fallback)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return value;
        }

        problems.Add($"{label} must be {expected}.");
        return fallback;
    }

    private static int ParseWhole(string? text, string label, List<string> problems, int fallback)
    {
        var trimmed = text?.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        problems.Add($"{label} must be a whole number.");
        return fallback;
    }
}
