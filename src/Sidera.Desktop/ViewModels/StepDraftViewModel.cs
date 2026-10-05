using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// One step of the sequence being edited: the text boxes and pickers of its parameters, and how the step is shown in
/// the list. Typing never throws and nothing is built here: <see cref="Read"/> turns the fields into an immutable
/// <see cref="SequenceStepDraft"/>, and the draft view model validates and builds those.
/// <para>
/// Every property whose name ends in "Text" is an input field; changing one raises <see cref="Edited"/>.
/// Numbers are accepted with the decimal separator of the user's culture or a dot, and shown with a dot.
/// </para>
/// </summary>
public abstract partial class StepDraftViewModel : ViewModelBase
{
    protected StepDraftViewModel(Guid id)
    {
        Id = id;
        Title = string.Empty;
        Summary = string.Empty;
        Problems = [];
    }

    /// <summary>Local to the editor; the same for the draft step and, while it runs, for its row in the running sequence.</summary>
    public Guid Id { get; }

    public abstract SequenceStepKind Kind { get; }

    /// <summary>Position among its siblings, starting at 1: in the sequence, or in its Repeat.</summary>
    [ObservableProperty]
    public partial int Number { get; internal set; }

    /// <summary>The number as shown: "2" for a step of the sequence, "2.1" for the first step in step 2.</summary>
    [ObservableProperty]
    public partial string NumberLabel { get; internal set; } = string.Empty;

    /// <summary>
    /// The container this step is in (a Repeat, a Rig Track, a Multi-Rig block), or <c>null</c> for a step of the
    /// sequence itself.
    /// </summary>
    public ContainerStepDraftViewModel? Parent { get; internal set; }

    public bool IsTopLevel => Parent is null;
    public bool IsChild => Parent is not null;

    /// <summary>How many containers the step is inside of: 0 for a step of the sequence itself.</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>The step holds other steps (a Repeat, a Rig Track, a Multi-Rig block).</summary>
    public virtual bool IsContainer => false;

    /// <summary>The step is a Multi-Rig block: the workflow shows it as the head of its lanes.</summary>
    public bool IsMultiRig => Kind == SequenceStepKind.MultiRig;

    /// <summary>The step is a Rig Track: a lane of a Multi-Rig block.</summary>
    public bool IsTrack => Kind == SequenceStepKind.RigTrack;

    /// <summary>The step is inside a Rig Track, however deep: it belongs to a lane.</summary>
    public bool InTrack => Parent is { } parent && (parent.IsTrack || parent.InTrack);

    /// <summary>The row is shown by the view of the session that is selected (all rows are in the overview). Set by the draft.</summary>
    [ObservableProperty]
    public partial bool IsInScopeView { get; internal set; } = true;

    /// <summary>Whose step it is, as the list says: the name of the rig, "Shared" (mount, guider, delay), "Device" (a step that names its device) or "Parallel".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScopeText))]
    public partial string ScopeText { get; internal set; } = string.Empty;

    public bool HasScopeText => ScopeText.Length > 0;

    /// <summary>How far the row is indented in the list.</summary>
    public double IndentWidth => Depth * 28;

    // What the list shows while a step is dragged. The draft sets these; they are not part of the step.

    /// <summary>This is the step being dragged.</summary>
    [ObservableProperty]
    public partial bool IsDragSource { get; internal set; }

    /// <summary>The dragged step would be put before this row: the insertion line is at its top edge.</summary>
    [ObservableProperty]
    public partial bool ShowsDropBefore { get; internal set; }

    /// <summary>The dragged step would be put after this row (and what it holds): the insertion line is at its bottom edge.</summary>
    [ObservableProperty]
    public partial bool ShowsDropAfter { get; internal set; }

    /// <summary>The pointer is over this row with a step that may not go next to it.</summary>
    [ObservableProperty]
    public partial bool IsDropRejected { get; internal set; }

    /// <summary>How far the insertion line is indented: as far as the row it is next to.</summary>
    [ObservableProperty]
    public partial double DropIndentWidth { get; internal set; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>The parameters on one line, for example "Main Camera · 300 s".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; }

    /// <summary>What is wrong with this step: fields that are not numbers, then what validation found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyPropertyChangedFor(nameof(FirstProblem))]
    public partial IReadOnlyList<string> Problems { get; private set; }

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The first problem, shown on the row.</summary>
    public string? FirstProblem => Problems.Count > 0 ? Problems[0] : null;

    /// <summary>Raised after an input field or picker of this step changed.</summary>
    public event EventHandler? Edited;

    /// <summary>
    /// The step with the values of the fields. A field that is not a number reads as a harmless stand-in and is
    /// reported in <paramref name="parseErrors"/> instead, so the problem is never hidden behind a made-up value.
    /// </summary>
    internal abstract SequenceStepDraft Read(List<string> parseErrors);

    /// <summary>Pickers of this step, so that the registry can be read again.</summary>
    internal virtual IEnumerable<DevicePickerViewModel> Pickers => [];

    /// <summary>Filter choices of this step, so that the names of the slots can be looked up again.</summary>
    internal virtual IEnumerable<FilterChoiceViewModel> FilterChoices => [];

    /// <summary>Rig pickers of this step, so that the rig registry can be read again.</summary>
    internal virtual IEnumerable<RigPickerViewModel> RigPickers => [];

    internal void Show(StepDescription description, IReadOnlyList<string> problems)
    {
        Title = description.Title;
        Summary = description.Summary;
        if (!problems.SequenceEqual(Problems))
        {
            Problems = problems;
        }

        OnShown();
    }

    /// <summary>
    /// Called each time the step was described again (after every edit): what a step derives from its children is read
    /// again here. A property raised from here must not end in "Text": those are input fields, and changing one counts as
    /// an edit, which describes the step again.
    /// </summary>
    protected virtual void OnShown()
    {
    }

    protected void NotifyEdited() => Edited?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && name.EndsWith("Text", StringComparison.Ordinal))
        {
            NotifyEdited();
        }
    }

    protected DevicePickerViewModel Picker(DeviceRegistry registry, Func<IDevice, bool> accepts, DeviceId? initial)
    {
        var picker = new DevicePickerViewModel(registry, accepts, initial);
        picker.Changed += (_, _) => NotifyEdited();
        return picker;
    }

    protected static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // The user's own decimal separator, or a dot.
    protected static double ParseNumber(string? text, string label, string expected, List<string> errors, double fallback)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return value;
        }

        errors.Add($"{label} must be {expected}.");
        return fallback;
    }

    // A whole number: a focuser position. A field that is not one is reported, and reads as the stand-in.
    protected static int ParseWhole(string? text, string label, List<string> errors, int fallback)
    {
        var trimmed = text?.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        errors.Add($"{label} must be a whole number.");
        return fallback;
    }

    protected static bool IsFocuser(IDevice device) => device is IFocuser;
    protected static bool IsFilterWheel(IDevice device) => device is IFilterWheel;
    protected static bool IsCamera(IDevice device) => device is ICamera;
    protected static bool IsMount(IDevice device) => device is IMount;
    protected static bool IsGuider(IDevice device) => device is IGuider;
}

/// <summary>A step that holds other steps. Its <see cref="Children"/> have it as their <see cref="StepDraftViewModel.Parent"/>.</summary>
public abstract class ContainerStepDraftViewModel : StepDraftViewModel
{
    protected ContainerStepDraftViewModel(Guid id, IEnumerable<StepDraftViewModel> children) : base(id)
    {
        Children = [];
        foreach (var child in children)
        {
            child.Parent = this;
            Children.Add(child);
        }
    }

    /// <summary>The steps inside, in order.</summary>
    public ObservableCollection<StepDraftViewModel> Children { get; }

    public override bool IsContainer => true;
}

/// <summary>
/// A Repeat: runs its <see cref="ContainerStepDraftViewModel.Children"/> in order, as many times as the count says.
/// The children are ordinary leaf step view models, edited with the same editors as the steps of the sequence. The
/// draft view model reads them; this one only reads its own count.
/// </summary>
public sealed partial class RepeatStepDraftViewModel : ContainerStepDraftViewModel
{
    public RepeatStepDraftViewModel(RepeatStepDraft draft, IEnumerable<StepDraftViewModel> children)
        : base(draft.Id, children)
    {
        CountText = draft.Count.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Repeat;

    /// <summary>How many times the steps inside run.</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    /// <summary>The Repeat is inside a Rig Track: what it may hold is what a track may hold.</summary>
    public bool IsInTrack => Parent is RigTrackDraftViewModel;

    internal int ReadCount(List<string> parseErrors)
    {
        var text = CountText?.Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out count))
        {
            return count;
        }

        parseErrors.Add("Repeat count must be a whole number.");
        return 1;
    }

    // Without its children: the draft view model reads those, with their own problems.
    internal override SequenceStepDraft Read(List<string> parseErrors) => new RepeatStepDraft(Id, ReadCount(parseErrors), []);
}

/// <summary>
/// Imaging with several rigs at once. Its children are the Rig Tracks, one per rig; the mount and guider they share
/// are those of the session, not of the block.
/// </summary>
public sealed partial class MultiRigStepDraftViewModel : ContainerStepDraftViewModel
{
    private readonly bool _constructed;

    public MultiRigStepDraftViewModel(MultiRigStepDraft draft, IEnumerable<StepDraftViewModel> tracks, RigPickerViewModel triggerRig)
        : base(draft.Id, tracks)
    {
        var policy = draft.DitherPolicy ?? MultiRigDitherPolicyDraft.Default;
        DitherEnabled = policy.Enabled;
        DitherEveryText = policy.EveryNFrames.ToString(CultureInfo.InvariantCulture);
        DitherAmplitudeText = Format(policy.AmplitudePixels);
        DitherSettleThresholdText = Format(policy.SettleThresholdPixels);
        DitherSettleStableText = Format(policy.SettleStableSeconds);
        DitherSettleTimeoutText = Format(policy.SettleTimeoutSeconds);

        // Only the rigs of the tracks can trigger.
        TriggerRig = triggerRig;
        triggerRig.LimitTo = TrackRigIds;
        triggerRig.Changed += (_, _) => NotifyEdited();
        _constructed = true;
    }

    public override SequenceStepKind Kind => SequenceStepKind.MultiRig;

    /// <summary>The Rig Tracks as lanes: what each rig does, one summary for each, for the overview of the block.</summary>
    public IReadOnlyList<LaneSummary> Lanes => Children.OfType<RigTrackDraftViewModel>().Select(track => track.Lane).ToList();

    public bool HasLanes => Children.OfType<RigTrackDraftViewModel>().Any();

    /// <summary>The dither policy in one line ("Every 3 Wide Rig frames · 1.5 px · settle ≤ 0.5 px for 1 s"), or "Off".</summary>
    public string DitherSummary
    {
        get
        {
            if (!DitherEnabled)
            {
                return "Off";
            }

            var rig = TriggerRig.Selected?.Name ?? "trigger rig";
            var every = DitherEveryText?.Trim() == "1" ? $"After every {rig} frame" : $"Every {DitherEveryText?.Trim()} {rig} frames";
            return $"{every} · {DitherAmplitudeText?.Trim()} px · settle ≤ {DitherSettleThresholdText?.Trim()} px for {DitherSettleStableText?.Trim()} s";
        }
    }

    protected override void OnShown()
    {
        OnPropertyChanged(nameof(Lanes));
        OnPropertyChanged(nameof(HasLanes));
        OnPropertyChanged(nameof(DitherSummary));
    }

    /// <summary>The rig whose frames are counted for the dither policy: one of the rigs of the tracks.</summary>
    public RigPickerViewModel TriggerRig { get; }

    /// <summary>The block dithers the shared mount (the other fields only count when it is on).</summary>
    [ObservableProperty]
    public partial bool DitherEnabled { get; set; }

    /// <summary>A dither is requested after this many completed frames of the trigger rig.</summary>
    [ObservableProperty]
    public partial string DitherEveryText { get; set; } = string.Empty;

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    [ObservableProperty]
    public partial string DitherAmplitudeText { get; set; } = string.Empty;

    /// <summary>Guide error, in guide camera pixels, at or below which guiding counts as settled.</summary>
    [ObservableProperty]
    public partial string DitherSettleThresholdText { get; set; } = string.Empty;

    /// <summary>How long the guide error must stay within the threshold, in seconds.</summary>
    [ObservableProperty]
    public partial string DitherSettleStableText { get; set; } = string.Empty;

    /// <summary>How long to wait for guiding to settle before giving up, in seconds.</summary>
    [ObservableProperty]
    public partial string DitherSettleTimeoutText { get; set; } = string.Empty;

    internal override IEnumerable<RigPickerViewModel> RigPickers => [TriggerRig];

    partial void OnDitherEnabledChanged(bool value)
    {
        if (!_constructed)
        {
            return;
        }

        // Switching it on with no trigger yet starts from the first track, which is easy to change.
        if (value && TriggerRig.SelectedId is null
            && Children.OfType<RigTrackDraftViewModel>().Select(track => track.Rig.SelectedId).FirstOrDefault(id => id is not null) is { } first)
        {
            TriggerRig.Refresh();
            TriggerRig.Reset(first);
        }

        NotifyEdited();
    }

    // The rigs of the tracks, as far as they are selected.
    private IReadOnlySet<RigId> TrackRigIds() =>
        Children.OfType<RigTrackDraftViewModel>().Select(track => track.Rig.SelectedId).OfType<RigId>().ToHashSet();

    /// <summary>
    /// The policy as the fields say it. A field that is no number reads as a stand-in and is reported, as long as the
    /// policy is on; a policy that is off is not looked at, so a field of it that is no number reads as its default.
    /// </summary>
    internal MultiRigDitherPolicyDraft ReadPolicy(List<string> parseErrors)
    {
        var defaults = MultiRigDitherPolicyDraft.Default;
        var errors = DitherEnabled ? parseErrors : [];
        var on = DitherEnabled;

        var everyText = DitherEveryText?.Trim();
        var every = int.TryParse(everyText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var count)
            || int.TryParse(everyText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                ? count
                : ReportInterval(errors, on ? 1 : defaults.EveryNFrames);

        return new MultiRigDitherPolicyDraft(
            on,
            TriggerRig.SelectedId,
            every,
            ParseNumber(DitherAmplitudeText, "Dither amplitude", "a number of pixels", errors, on ? 1 : defaults.AmplitudePixels),
            ParseNumber(DitherSettleThresholdText, "Settle threshold", "a number of pixels", errors, on ? 1 : defaults.SettleThresholdPixels),
            ParseNumber(DitherSettleStableText, "Settle stable time", "a number of seconds", errors, on ? double.Epsilon : defaults.SettleStableSeconds),
            ParseNumber(DitherSettleTimeoutText, "Settle timeout", "a number of seconds", errors, on ? double.MaxValue : defaults.SettleTimeoutSeconds));
    }

    private static int ReportInterval(List<string> errors, int standIn)
    {
        errors.Add("Dither interval must be a whole number of frames.");
        return standIn;
    }

    // Without its tracks: the draft view model reads those.
    internal override SequenceStepDraft Read(List<string> parseErrors) => new MultiRigStepDraft(Id, [], ReadPolicy(parseErrors));
}

/// <summary>
/// One Rig Track: the rig it images with, and the steps that run on it. What a rig has (today: its camera) is what the
/// steps of the track use, so those steps select no equipment of their own.
/// </summary>
public sealed partial class RigTrackDraftViewModel : ContainerStepDraftViewModel
{
    private readonly bool _constructed;

    public RigTrackDraftViewModel(RigTrackDraft draft, IEnumerable<StepDraftViewModel> steps, RigPickerViewModel rig)
        : base(draft.Id, steps)
    {
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();

        var policy = draft.AutofocusPolicy ?? RigAutofocusPolicyDraft.Default;
        AutofocusEnabled = policy.Enabled;
        AutofocusAtStart = policy.AtTrackStart;
        AutofocusAfterFilterChange = policy.AfterFilterChange;
        AutofocusExposureText = Format(policy.ExposureSeconds);
        AutofocusStepSizeText = policy.StepSize.ToString(CultureInfo.InvariantCulture);
        AutofocusSamplesText = policy.SampleCount.ToString(CultureInfo.InvariantCulture);
        _constructed = true;
    }

    /// <summary>The rig of this track focuses by itself (the triggers and settings only count when it is on).</summary>
    [ObservableProperty]
    public partial bool AutofocusEnabled { get; set; }

    /// <summary>Focus once at the start of the track.</summary>
    [ObservableProperty]
    public partial bool AutofocusAtStart { get; set; }

    /// <summary>Focus after every Change Filter step of the track.</summary>
    [ObservableProperty]
    public partial bool AutofocusAfterFilterChange { get; set; }

    /// <summary>The exposure at each sample position, in seconds.</summary>
    [ObservableProperty]
    public partial string AutofocusExposureText { get; set; } = string.Empty;

    /// <summary>The distance between two sample positions, in focuser steps.</summary>
    [ObservableProperty]
    public partial string AutofocusStepSizeText { get; set; } = string.Empty;

    /// <summary>How many positions are sampled; odd.</summary>
    [ObservableProperty]
    public partial string AutofocusSamplesText { get; set; } = string.Empty;

    partial void OnAutofocusEnabledChanged(bool value) => EditedByUser();

    partial void OnAutofocusAtStartChanged(bool value) => EditedByUser();

    partial void OnAutofocusAfterFilterChangeChanged(bool value) => EditedByUser();

    private void EditedByUser()
    {
        if (_constructed)
        {
            NotifyEdited();
        }
    }

    /// <summary>
    /// The policy as the fields say it. A field that is no number reads as a stand-in and is reported, as long as the
    /// policy is on; a policy that is off is not looked at, so a field of it that is no number reads as its default.
    /// </summary>
    internal RigAutofocusPolicyDraft ReadPolicy(List<string> parseErrors)
    {
        var defaults = RigAutofocusPolicyDraft.Default;
        var on = AutofocusEnabled;
        var errors = on ? parseErrors : [];

        return new RigAutofocusPolicyDraft(
            on,
            AutofocusAtStart,
            AutofocusAfterFilterChange,
            ParseNumber(AutofocusExposureText, "Autofocus exposure", "a number of seconds", errors, on ? 1 : defaults.ExposureSeconds),
            ParseWhole(AutofocusStepSizeText, "Autofocus step size", errors, on ? 1 : defaults.StepSize),
            ParseWhole(AutofocusSamplesText, "Autofocus samples", errors, on ? AutofocusOptions.MinimumSampleCount : defaults.SampleCount));
    }

    public override SequenceStepKind Kind => SequenceStepKind.RigTrack;

    public RigPickerViewModel Rig { get; }

    /// <summary>What this track does, for the overview of its Multi-Rig block.</summary>
    public LaneSummary Lane
    {
        get
        {
            var lines = new List<LaneLine>();
            foreach (var child in Children)
            {
                lines.Add(new LaneLine(child is RepeatStepDraftViewModel ? child.Title : Describe(child), false));
                if (child is RepeatStepDraftViewModel repeat)
                {
                    lines.AddRange(repeat.Children.Select(inner => new LaneLine(Describe(inner), true)));
                }
            }

            return new LaneSummary(Rig.Selected?.Name ?? "No rig selected", lines, AutofocusPolicySummary, HasProblems);
        }
    }

    /// <summary>"Autofocus: track start + filter change", or <c>null</c> when the rig of the track does not focus by itself.</summary>
    public string? AutofocusPolicySummary => !AutofocusEnabled ? null
        : "Autofocus: " + (AutofocusAtStart && AutofocusAfterFilterChange ? "track start + filter change"
            : AutofocusAtStart ? "track start"
            : AutofocusAfterFilterChange ? "filter change"
            : "no trigger");

    private static string Describe(StepDraftViewModel step) =>
        string.IsNullOrWhiteSpace(step.Summary) ? step.Title : $"{step.Title} · {step.Summary}";

    protected override void OnShown()
    {
        OnPropertyChanged(nameof(Lane));
        OnPropertyChanged(nameof(AutofocusPolicySummary));
    }

    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];

    // Without its steps: the draft view model reads those.
    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new RigTrackDraft(Id, Rig.SelectedId, [], ReadPolicy(parseErrors));
}

/// <summary>An exposure inside a Rig Track; its camera is that of the rig of the track.</summary>
public sealed partial class RigExposureStepDraftViewModel : StepDraftViewModel
{
    public RigExposureStepDraftViewModel(RigExposureStepDraft draft, Func<AcquisitionCameraContext>? camera = null) : base(draft.Id)
    {
        ExposureText = Format(draft.Seconds);
        Acquisition = new AcquisitionEditorViewModel(draft.Acquisition, camera ?? (() => AcquisitionCameraContext.None));
        Acquisition.Edited += (_, _) => NotifyEdited();
    }

    /// <summary>What the exposure sets besides its duration, for the camera of the rig of the track.</summary>
    public AcquisitionEditorViewModel Acquisition { get; }

    protected override void OnShown() => Acquisition.Refresh();

    public override SequenceStepKind Kind => SequenceStepKind.RigExposure;

    /// <summary>Exposure time in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new RigExposureStepDraft(Id, ParseNumber(ExposureText, "Exposure", "a number of seconds", parseErrors, 1))
        {
            Acquisition = Acquisition.Read(parseErrors),
        };
}

public sealed partial class ExposureStepDraftViewModel : StepDraftViewModel
{
    public ExposureStepDraftViewModel(DeviceRegistry registry, ExposureStepDraft draft) : base(draft.Id)
    {
        Camera = Picker(registry, IsCamera, draft.CameraId);
        ExposureText = Format(draft.Seconds);
        Acquisition = new AcquisitionEditorViewModel(draft.Acquisition, () => CameraContext(registry, Camera.SelectedId));
        Acquisition.Edited += (_, _) => NotifyEdited();
        Camera.Changed += (_, _) => Acquisition.Refresh();
    }

    /// <summary>What the exposure sets besides its duration: only what the selected camera supports is offered.</summary>
    public AcquisitionEditorViewModel Acquisition { get; }

    /// <summary>The camera as the acquisition editor sees it: its id and, when it is connected, what it supports.</summary>
    internal static AcquisitionCameraContext CameraContext(DeviceRegistry registry, DeviceId? cameraId) =>
        cameraId is { } id && registry.TryGet(id, out var device) && device is ICapable<CameraCapabilities> capable
            ? new AcquisitionCameraContext(id, capable.Capabilities)
            : new AcquisitionCameraContext(cameraId, DeviceCapabilities<CameraCapabilities>.Unknown);

    protected override void OnShown() => Acquisition.Refresh();

    public override SequenceStepKind Kind => SequenceStepKind.Exposure;
    public DevicePickerViewModel Camera { get; }

    /// <summary>Exposure time in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Camera];

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new ExposureStepDraft(Id, Camera.SelectedId, ParseNumber(ExposureText, "Exposure", "a number of seconds", parseErrors, 1))
        {
            Acquisition = Acquisition.Read(parseErrors),
        };
}

public sealed partial class DelayStepDraftViewModel : StepDraftViewModel
{
    public DelayStepDraftViewModel(DelayStepDraft draft) : base(draft.Id)
    {
        DurationText = Format(draft.Seconds);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Delay;

    /// <summary>How long to wait, in seconds.</summary>
    [ObservableProperty]
    public partial string DurationText { get; set; } = string.Empty;

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new DelayStepDraft(Id, ParseNumber(DurationText, "Delay", "a number of seconds", parseErrors, 1));
}

public sealed partial class SlewStepDraftViewModel : StepDraftViewModel
{
    public SlewStepDraftViewModel(DeviceRegistry registry, SlewStepDraft draft) : base(draft.Id)
    {
        Mount = Picker(registry, IsMount, draft.MountId);
        RightAscensionText = Format(draft.RightAscensionHours);
        DeclinationText = Format(draft.DeclinationDegrees);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Slew;
    public DevicePickerViewModel Mount { get; }

    /// <summary>Right ascension of the target, in hours.</summary>
    [ObservableProperty]
    public partial string RightAscensionText { get; set; } = string.Empty;

    /// <summary>Declination of the target, in degrees.</summary>
    [ObservableProperty]
    public partial string DeclinationText { get; set; } = string.Empty;

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Mount];

    internal override SequenceStepDraft Read(List<string> parseErrors) => new SlewStepDraft(
        Id, Mount.SelectedId,
        ParseNumber(RightAscensionText, "Right ascension", "a number of hours", parseErrors, 0),
        ParseNumber(DeclinationText, "Declination", "a number of degrees", parseErrors, 0));
}

public sealed class StartGuidingStepDraftViewModel : StepDraftViewModel
{
    public StartGuidingStepDraftViewModel(DeviceRegistry registry, StartGuidingStepDraft draft) : base(draft.Id)
    {
        Guider = Picker(registry, IsGuider, draft.GuiderId);
    }

    public override SequenceStepKind Kind => SequenceStepKind.StartGuiding;
    public DevicePickerViewModel Guider { get; }

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Guider];

    internal override SequenceStepDraft Read(List<string> parseErrors) => new StartGuidingStepDraft(Id, Guider.SelectedId);
}

public sealed class StopGuidingStepDraftViewModel : StepDraftViewModel
{
    public StopGuidingStepDraftViewModel(DeviceRegistry registry, StopGuidingStepDraft draft) : base(draft.Id)
    {
        Guider = Picker(registry, IsGuider, draft.GuiderId);
    }

    public override SequenceStepKind Kind => SequenceStepKind.StopGuiding;
    public DevicePickerViewModel Guider { get; }

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Guider];

    internal override SequenceStepDraft Read(List<string> parseErrors) => new StopGuidingStepDraft(Id, Guider.SelectedId);
}

public sealed partial class DitherStepDraftViewModel : StepDraftViewModel
{
    public DitherStepDraftViewModel(DeviceRegistry registry, DitherStepDraft draft) : base(draft.Id)
    {
        Guider = Picker(registry, IsGuider, draft.GuiderId);
        Mount = Picker(registry, IsMount, draft.MountId);
        Camera = Picker(registry, IsCamera, draft.CameraId);
        AmplitudeText = Format(draft.AmplitudePixels);
        SettleThresholdText = Format(draft.SettleThresholdPixels);
        SettleStableText = Format(draft.SettleStableSeconds);
        SettleTimeoutText = Format(draft.SettleTimeoutSeconds);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Dither;
    public DevicePickerViewModel Guider { get; }
    public DevicePickerViewModel Mount { get; }

    /// <summary>The camera the dither disturbs.</summary>
    public DevicePickerViewModel Camera { get; }

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    [ObservableProperty]
    public partial string AmplitudeText { get; set; } = string.Empty;

    /// <summary>Guide error, in guide camera pixels, at or below which guiding counts as settled.</summary>
    [ObservableProperty]
    public partial string SettleThresholdText { get; set; } = string.Empty;

    /// <summary>How long the guide error must stay within the threshold, in seconds.</summary>
    [ObservableProperty]
    public partial string SettleStableText { get; set; } = string.Empty;

    /// <summary>How long to wait for guiding to settle before giving up, in seconds.</summary>
    [ObservableProperty]
    public partial string SettleTimeoutText { get; set; } = string.Empty;

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Guider, Mount, Camera];

    // Stand-ins for unreadable settle fields keep "timeout longer than stable" true, so that an unreadable field
    // only ever produces its own message.
    internal override SequenceStepDraft Read(List<string> parseErrors) => new DitherStepDraft(
        Id, Guider.SelectedId, Mount.SelectedId, Camera.SelectedId,
        ParseNumber(AmplitudeText, "Dither amplitude", "a number of pixels", parseErrors, 1),
        ParseNumber(SettleThresholdText, "Settle threshold", "a number of pixels", parseErrors, 1),
        ParseNumber(SettleStableText, "Settle stable time", "a number of seconds", parseErrors, double.Epsilon),
        ParseNumber(SettleTimeoutText, "Settle timeout", "a number of seconds", parseErrors, double.MaxValue));
}

/// <summary>Moves one selected focuser to an absolute position, in focuser steps.</summary>
public sealed partial class MoveFocuserStepDraftViewModel : StepDraftViewModel
{
    private readonly DeviceRegistry _registry;

    public MoveFocuserStepDraftViewModel(DeviceRegistry registry, MoveFocuserStepDraft draft) : base(draft.Id)
    {
        _registry = registry;
        Focuser = Picker(registry, IsFocuser, draft.FocuserId);
        Focuser.Changed += (_, _) => OnPropertyChanged(nameof(RangeLabel));
        PositionText = draft.Position.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.MoveFocuser;
    public DevicePickerViewModel Focuser { get; }

    /// <summary>Target position in focuser steps.</summary>
    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    /// <summary>What the selected focuser can move to, for example "0 to 50000"; empty when it is not known.</summary>
    public string RangeLabel =>
        Focuser.SelectedId is { } id && _registry.TryGet(id, out var device) && device is IFocuser focuser
            ? string.Create(CultureInfo.InvariantCulture, $"{focuser.MinPosition} to {focuser.MaxPosition}")
            : string.Empty;

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Focuser];

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new MoveFocuserStepDraft(Id, Focuser.SelectedId, ParseWhole(PositionText, "Focuser position", parseErrors, 0));
}

/// <summary>Turns one selected filter wheel to a slot that the user picks by name.</summary>
public sealed class ChangeFilterStepDraftViewModel : StepDraftViewModel
{
    public ChangeFilterStepDraftViewModel(DeviceRegistry registry, ChangeFilterStepDraft draft) : base(draft.Id)
    {
        Wheel = Picker(registry, IsFilterWheel, draft.FilterWheelId);
        Filter = new FilterChoiceViewModel(
            () => Wheel.SelectedId is { } id && registry.TryGet(id, out var device) && device is IFilterWheel wheel ? wheel.Slots : null,
            draft.SlotIndex);
        Filter.Changed += (_, _) => NotifyEdited();
        Wheel.Changed += (_, _) => Filter.Refresh();
    }

    public override SequenceStepKind Kind => SequenceStepKind.ChangeFilter;
    public DevicePickerViewModel Wheel { get; }

    /// <summary>The slot to turn to, by name; what is kept is its index.</summary>
    public FilterChoiceViewModel Filter { get; }

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Wheel];
    internal override IEnumerable<FilterChoiceViewModel> FilterChoices => [Filter];

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new ChangeFilterStepDraft(Id, Wheel.SelectedId, Filter.SelectedIndex);
}

/// <summary>A focuser move inside a Rig Track: the focuser is that of the rig of the track.</summary>
public sealed partial class RigMoveFocuserStepDraftViewModel : StepDraftViewModel
{
    public RigMoveFocuserStepDraftViewModel(RigMoveFocuserStepDraft draft) : base(draft.Id)
    {
        PositionText = draft.Position.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.RigMoveFocuser;

    /// <summary>Target position in focuser steps.</summary>
    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new RigMoveFocuserStepDraft(Id, ParseWhole(PositionText, "Focuser position", parseErrors, 0));
}

/// <summary>A filter change inside a Rig Track: the filter wheel is that of the rig of the track.</summary>
public sealed class RigChangeFilterStepDraftViewModel : StepDraftViewModel
{
    /// <param name="slots">The slots of the filter wheel of the rig of the track this step is in; <c>null</c> when there is none.</param>
    public RigChangeFilterStepDraftViewModel(RigChangeFilterStepDraft draft, Func<IReadOnlyList<FilterSlot>?> slots)
        : base(draft.Id)
    {
        Filter = new FilterChoiceViewModel(slots, draft.SlotIndex);
        Filter.Changed += (_, _) => NotifyEdited();
    }

    public override SequenceStepKind Kind => SequenceStepKind.RigChangeFilter;

    /// <summary>The slot to turn to, by name; what is kept is its index.</summary>
    public FilterChoiceViewModel Filter { get; }

    internal override IEnumerable<FilterChoiceViewModel> FilterChoices => [Filter];

    internal override SequenceStepDraft Read(List<string> parseErrors) => new RigChangeFilterStepDraft(Id, Filter.SelectedIndex);
}

/// <summary>Focuses a rig that is picked by the step: its camera and its focuser, with the settings of the step.</summary>
public sealed partial class AutofocusStepDraftViewModel : StepDraftViewModel
{
    public AutofocusStepDraftViewModel(AutofocusStepDraft draft, RigPickerViewModel rig) : base(draft.Id)
    {
        Rig = rig;
        rig.Changed += (_, _) => NotifyEdited();
        ExposureText = Format(draft.ExposureSeconds);
        StepSizeText = draft.StepSize.ToString(CultureInfo.InvariantCulture);
        SamplesText = draft.SampleCount.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Autofocus;

    /// <summary>The rig that is focused.</summary>
    public RigPickerViewModel Rig { get; }

    /// <summary>The exposure at each sample position, in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    /// <summary>The distance between two sample positions, in focuser steps.</summary>
    [ObservableProperty]
    public partial string StepSizeText { get; set; } = string.Empty;

    /// <summary>How many positions are sampled; odd.</summary>
    [ObservableProperty]
    public partial string SamplesText { get; set; } = string.Empty;

    internal override IEnumerable<RigPickerViewModel> RigPickers => [Rig];

    internal override SequenceStepDraft Read(List<string> parseErrors) => new AutofocusStepDraft(
        Id, Rig.SelectedId,
        ParseNumber(ExposureText, "Autofocus exposure", "a number of seconds", parseErrors, 1),
        ParseWhole(StepSizeText, "Autofocus step size", parseErrors, 1),
        ParseWhole(SamplesText, "Autofocus samples", parseErrors, AutofocusOptions.MinimumSampleCount));
}

/// <summary>Autofocus inside a Rig Track: it focuses the rig of the track.</summary>
public sealed partial class RigAutofocusStepDraftViewModel : StepDraftViewModel
{
    public RigAutofocusStepDraftViewModel(RigAutofocusStepDraft draft) : base(draft.Id)
    {
        ExposureText = Format(draft.ExposureSeconds);
        StepSizeText = draft.StepSize.ToString(CultureInfo.InvariantCulture);
        SamplesText = draft.SampleCount.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.RigAutofocus;

    /// <summary>The exposure at each sample position, in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    /// <summary>The distance between two sample positions, in focuser steps.</summary>
    [ObservableProperty]
    public partial string StepSizeText { get; set; } = string.Empty;

    /// <summary>How many positions are sampled; odd.</summary>
    [ObservableProperty]
    public partial string SamplesText { get; set; } = string.Empty;

    internal override SequenceStepDraft Read(List<string> parseErrors) => new RigAutofocusStepDraft(
        Id,
        ParseNumber(ExposureText, "Autofocus exposure", "a number of seconds", parseErrors, 1),
        ParseWhole(StepSizeText, "Autofocus step size", parseErrors, 1),
        ParseWhole(SamplesText, "Autofocus samples", parseErrors, AutofocusOptions.MinimumSampleCount));
}
