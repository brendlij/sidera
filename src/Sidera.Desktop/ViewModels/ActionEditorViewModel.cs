using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// One action in the drawer: the few values it has, and nothing about devices. The action works with the imaging setup of the sequence it is in (or, in the preparation of a target and at the start and the
/// end of the session, with the setups that are imaged); only with several setups does an action of the session show "Use setup", the Advanced override, so that it can be told to work with one of them.
/// </summary>
public sealed partial class ActionEditorViewModel : ObservableObject, IUnreadableFields
{
    private readonly SessionEditorViewModel _owner;
    private readonly ActionOwner _place;
    private readonly ImagingBindingId? _context;
    private readonly bool _loading = true;
    private SessionAction _action;
    private List<string> _problems = [];

    internal ActionEditorViewModel(SessionEditorViewModel owner, SessionAction action, ActionOwner place, ImagingBindingId? context)
    {
        _owner = owner;
        _action = action;
        _place = place;
        _context = context;
        Id = action.Id;
        Kind = action.Kind;
        var info = ActionCatalog.Of(action.Kind);
        Title = info.Title;
        Description = info.Description;
        IsOn = action.Enabled;
        Conditions = new ConditionTogglesViewModel(Edited);

        switch (action)
        {
            case ExposureAction e:
                SecondsText = FieldParse.Text(e.Seconds);
                break;
            case WaitAction w:
                SecondsText = FieldParse.Text(w.Seconds);
                break;
            case PlateSolveAction p:
                SecondsText = FieldParse.Text(p.ExposureSeconds);
                break;
            case MoveFocuserAction m:
                PositionText = FieldParse.Text(m.Position);
                break;
            case SetFilterAction f:
                Filters = owner.FiltersOf(owner.ResolvedSetup(context)).Where(c => c.Slot is not null).ToList();
                SelectedFilter = Filters.FirstOrDefault(c => c.Slot == f.Slot);
                break;
            case AutofocusAction a:
                FocusExposureText = FieldParse.Text(a.Settings.ExposureSeconds);
                FocusStepText = FieldParse.Text(a.Settings.StepSize);
                FocusSamplesText = FieldParse.Text(a.Settings.SampleCount);
                break;
            case DitherNowAction d:
                DitherAmplitudeText = FieldParse.Text(d.Settings.AmplitudePixels);
                DitherThresholdText = FieldParse.Text(d.Settings.SettleThresholdPixels);
                DitherStableText = FieldParse.Text(d.Settings.SettleStableSeconds);
                DitherTimeoutText = FieldParse.Text(d.Settings.SettleTimeoutSeconds);
                break;
            case SlewAndCenterAction c:
                ToleranceText = FieldParse.Text(c.ToleranceArcseconds);
                AttemptsText = FieldParse.Text(c.MaxAttempts);
                SolveExposureText = FieldParse.Text(c.SolveExposureSeconds);
                break;
            case CenterAndRotateAction r:
                ToleranceText = FieldParse.Text(r.ToleranceArcseconds);
                AttemptsText = FieldParse.Text(r.MaxAttempts);
                SolveExposureText = FieldParse.Text(r.SolveExposureSeconds);
                break;
            case CoolCameraAction cool:
                CelsiusText = FieldParse.Text(cool.TargetCelsius);
                RampText = FieldParse.Text(cool.RampMinutes);
                break;
            case WarmCameraAction warm:
                RampText = FieldParse.Text(warm.RampMinutes);
                break;
            case SetTrackingAction tracking:
                TrackingOn = tracking.On;
                break;
            case WaitUntilAction until:
                Conditions.Load(until.Conditions);
                break;
        }

        // The override: only for the actions of the session, only when there is a choice, and never for an action that sits in the sequence of a setup.
        ShowsOverride = place.Place != ActionPlace.Block && owner.IsMultiSetup && Kind is not (SessionActionKind.Wait or SessionActionKind.WaitUntil);
        if (ShowsOverride)
        {
            OverrideChoices = [new SetupChoice(null, "The imaged setups", "what the session images with"), .. owner.SetupChoices()];
            SelectedOverride = OverrideChoices.FirstOrDefault(c => c.Id == (owner.ResolvedSetup(action.Setup) is { } rig ? SessionEditorViewModel.PathOf(rig) : action.Setup)) ?? OverrideChoices[0];
        }

        _loading = false;
    }

    public Guid Id { get; }

    public SessionActionKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    // ---- which fields

    public bool ShowsSeconds => Kind is SessionActionKind.Exposure or SessionActionKind.Wait or SessionActionKind.PlateSolve;

    public string SecondsLabel => Kind switch
    {
        SessionActionKind.Exposure => "Exposure",
        SessionActionKind.PlateSolve => "Solve exposure",
        _ => "Wait",
    };

    public bool ShowsPosition => Kind == SessionActionKind.MoveFocuser;

    public bool ShowsFilter => Kind == SessionActionKind.SetFilter;

    public bool ShowsFocus => Kind == SessionActionKind.Autofocus;

    public bool ShowsDither => Kind == SessionActionKind.DitherNow;

    public bool ShowsCentering => Kind is SessionActionKind.SlewAndCenter or SessionActionKind.CenterAndRotate;

    public bool ShowsCooling => Kind == SessionActionKind.CoolCamera;

    public bool ShowsRamp => Kind is SessionActionKind.CoolCamera or SessionActionKind.WarmCamera;

    public bool ShowsTracking => Kind == SessionActionKind.SetTracking;

    public bool ShowsConditions => Kind == SessionActionKind.WaitUntil;

    public bool ShowsOverride { get; }

    /// <summary>The action only needs to be switched on or off: it has nothing to set.</summary>
    public bool HasNothingToSet => !(ShowsSeconds || ShowsPosition || ShowsFilter || ShowsFocus || ShowsDither || ShowsCentering || ShowsRamp || ShowsTracking || ShowsConditions);

    /// <summary>The action belongs to a block: it runs for the setup of the sequence it is in.</summary>
    public bool InBlock => _place.Place == ActionPlace.Block;

    // ---- the values

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial string SecondsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PositionText { get; set; } = string.Empty;

    public IReadOnlyList<FilterChoice> Filters { get; } = [];

    [ObservableProperty]
    public partial FilterChoice? SelectedFilter { get; set; }

    public bool HasFilters => Filters.Count > 0;

    [ObservableProperty]
    public partial string FocusExposureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FocusStepText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FocusSamplesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherAmplitudeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherThresholdText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherStableText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DitherTimeoutText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ToleranceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttemptsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SolveExposureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CelsiusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RampText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TrackingOn { get; set; }

    /// <summary>What to wait for, all of it: the sky (darkness, the target above an altitude), the time.</summary>
    public ConditionTogglesViewModel Conditions { get; }

    public IReadOnlyList<SetupChoice> OverrideChoices { get; } = [];

    [ObservableProperty]
    public partial SetupChoice? SelectedOverride { get; set; }

    // ---- problems

    public string ProblemText => string.Join(" ", _problems.Concat(Conditions.Problems).Append(_owner.ProblemTextOf(Id)).Where(p => p.Length > 0).Distinct());

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasUnreadableFields => _problems.Count > 0 || Conditions.Problems.Count > 0;

    partial void OnIsOnChanged(bool value) => Edited();

    partial void OnSecondsTextChanged(string value) => Edited();

    partial void OnPositionTextChanged(string value) => Edited();

    partial void OnSelectedFilterChanged(FilterChoice? value) => Edited();

    partial void OnFocusExposureTextChanged(string value) => Edited();

    partial void OnFocusStepTextChanged(string value) => Edited();

    partial void OnFocusSamplesTextChanged(string value) => Edited();

    partial void OnDitherAmplitudeTextChanged(string value) => Edited();

    partial void OnDitherThresholdTextChanged(string value) => Edited();

    partial void OnDitherStableTextChanged(string value) => Edited();

    partial void OnDitherTimeoutTextChanged(string value) => Edited();

    partial void OnToleranceTextChanged(string value) => Edited();

    partial void OnAttemptsTextChanged(string value) => Edited();

    partial void OnSolveExposureTextChanged(string value) => Edited();

    partial void OnCelsiusTextChanged(string value) => Edited();

    partial void OnRampTextChanged(string value) => Edited();

    partial void OnTrackingOnChanged(bool value) => Edited();

    partial void OnSelectedOverrideChanged(SetupChoice? value) => Edited();

    // What to wait for: the switches of the sky, and the time of day when that is on (it has its own field, read by itself).
    private IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> ReadUntil(List<string> problems)
    {
        var list = Conditions.BuildStart().ToList();
        problems.AddRange(Conditions.Problems);
        if (Conditions.TimeOn)
        {
            list.Add(Conditions.BuildTime());
            problems.AddRange(Conditions.Problems);
        }

        return list;
    }

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        SessionAction built = _action switch
        {
            ExposureAction e => e with { Seconds = FieldParse.Number(SecondsText, "The exposure", "a number of seconds", problems, e.Seconds) },
            WaitAction w => w with { Seconds = FieldParse.Number(SecondsText, "The wait", "a number of seconds", problems, w.Seconds) },
            PlateSolveAction p => p with { ExposureSeconds = FieldParse.Number(SecondsText, "The solve exposure", "a number of seconds", problems, p.ExposureSeconds) },
            MoveFocuserAction m => m with { Position = FieldParse.Whole(PositionText, "The focuser position", problems, m.Position) },
            SetFilterAction f => SelectedFilter?.Slot is { } slot ? f with { Slot = slot } : f,
            AutofocusAction a => a with
            {
                Settings = new FocusSettings(
                    FieldParse.Number(FocusExposureText, "The autofocus exposure", "a number of seconds", problems, a.Settings.ExposureSeconds),
                    FieldParse.Whole(FocusStepText, "The autofocus step size", problems, a.Settings.StepSize),
                    FieldParse.Whole(FocusSamplesText, "The autofocus samples", problems, a.Settings.SampleCount)),
            },
            DitherNowAction d => d with
            {
                Settings = new DitherSettings(
                    FieldParse.Number(DitherAmplitudeText, "The dither amplitude", "a number of pixels", problems, d.Settings.AmplitudePixels),
                    FieldParse.Number(DitherThresholdText, "The settle threshold", "a number of pixels", problems, d.Settings.SettleThresholdPixels),
                    FieldParse.Number(DitherStableText, "The settle time", "a number of seconds", problems, d.Settings.SettleStableSeconds),
                    FieldParse.Number(DitherTimeoutText, "The settle timeout", "a number of seconds", problems, d.Settings.SettleTimeoutSeconds)),
            },
            SlewAndCenterAction c => c with
            {
                ToleranceArcseconds = FieldParse.Number(ToleranceText, "The tolerance", "a number of arcseconds", problems, c.ToleranceArcseconds),
                MaxAttempts = FieldParse.Whole(AttemptsText, "The attempts", problems, c.MaxAttempts),
                SolveExposureSeconds = FieldParse.Number(SolveExposureText, "The solve exposure", "a number of seconds", problems, c.SolveExposureSeconds),
            },
            CenterAndRotateAction r => r with
            {
                ToleranceArcseconds = FieldParse.Number(ToleranceText, "The tolerance", "a number of arcseconds", problems, r.ToleranceArcseconds),
                MaxAttempts = FieldParse.Whole(AttemptsText, "The attempts", problems, r.MaxAttempts),
                SolveExposureSeconds = FieldParse.Number(SolveExposureText, "The solve exposure", "a number of seconds", problems, r.SolveExposureSeconds),
            },
            CoolCameraAction cool => cool with
            {
                TargetCelsius = FieldParse.Number(CelsiusText, "The temperature", "a number of degrees", problems, cool.TargetCelsius),
                RampMinutes = Math.Max(0, FieldParse.Number(RampText, "The ramp", "a number of minutes", problems, cool.RampMinutes)),
            },
            WarmCameraAction warm => warm with { RampMinutes = Math.Max(0, FieldParse.Number(RampText, "The ramp", "a number of minutes", problems, warm.RampMinutes)) },
            SetTrackingAction t => t with { On = TrackingOn },
            WaitUntilAction until => until with { Conditions = ReadUntil(problems) },
            _ => _action,
        };

        _problems = problems;
        var setup = ShowsOverride ? SelectedOverride?.Id : _action.Setup;
        built = built with { Enabled = IsOn, Setup = setup };
        _action = built;
        _owner.EditAction(Id, _ => built);
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasUnreadableFields));
    }

    // ---- commands

    /// <summary>Back to the block the action is in.</summary>
    [RelayCommand]
    private void BackToBlock()
    {
        if (_place is { Place: ActionPlace.Block, Owner: { } block })
        {
            _owner.SelectBlock(block);
        }
        else
        {
            _owner.ClearSelection();
        }
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveAction(Id);

    public bool CanGoBack => InBlock;

    public string BackText => InBlock ? "← Block" : "Close";

    private bool IsEditable => _owner.IsEditable;
}
