using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Runtime.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

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

    /// <summary>The Repeat this step is in, or <c>null</c> for a step of the sequence itself.</summary>
    public RepeatStepDraftViewModel? Parent { get; internal set; }

    public bool IsTopLevel => Parent is null;
    public bool IsChild => Parent is not null;

    /// <summary>The step holds other steps (a Repeat).</summary>
    public virtual bool IsContainer => false;

    /// <summary>How far the row is indented in the list.</summary>
    public double IndentWidth => Parent is null ? 0 : 28;

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

    internal void Show(StepDescription description, IReadOnlyList<string> problems)
    {
        Title = description.Title;
        Summary = description.Summary;
        if (!problems.SequenceEqual(Problems))
        {
            Problems = problems;
        }
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

    protected static bool IsCamera(IDevice device) => device is ICamera;
    protected static bool IsMount(IDevice device) => device is IMount;
    protected static bool IsGuider(IDevice device) => device is IGuider;
}

/// <summary>
/// A Repeat: runs its <see cref="Children"/> in order, as many times as the count says. The children are ordinary
/// leaf step view models with this Repeat as <see cref="StepDraftViewModel.Parent"/>, edited with the same editors
/// as the steps of the sequence. The draft view model reads them; this one only reads its own count.
/// </summary>
public sealed partial class RepeatStepDraftViewModel : StepDraftViewModel
{
    public RepeatStepDraftViewModel(RepeatStepDraft draft, IEnumerable<StepDraftViewModel> children) : base(draft.Id)
    {
        Children = [];
        foreach (var child in children)
        {
            child.Parent = this;
            Children.Add(child);
        }

        CountText = draft.Count.ToString(CultureInfo.InvariantCulture);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Repeat;
    public override bool IsContainer => true;

    /// <summary>The steps inside, in order.</summary>
    public ObservableCollection<StepDraftViewModel> Children { get; }

    /// <summary>How many times the steps inside run.</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

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

public sealed partial class ExposureStepDraftViewModel : StepDraftViewModel
{
    public ExposureStepDraftViewModel(DeviceRegistry registry, ExposureStepDraft draft) : base(draft.Id)
    {
        Camera = Picker(registry, IsCamera, draft.CameraId);
        ExposureText = Format(draft.Seconds);
    }

    public override SequenceStepKind Kind => SequenceStepKind.Exposure;
    public DevicePickerViewModel Camera { get; }

    /// <summary>Exposure time in seconds.</summary>
    [ObservableProperty]
    public partial string ExposureText { get; set; } = string.Empty;

    internal override IEnumerable<DevicePickerViewModel> Pickers => [Camera];

    internal override SequenceStepDraft Read(List<string> parseErrors) =>
        new ExposureStepDraft(Id, Camera.SelectedId, ParseNumber(ExposureText, "Exposure", "a number of seconds", parseErrors, 1));
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
