using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// A block in the drawer, in four parts: its actions (in order; from the first exposure on they repeat), how it repeats, what it does by itself (dither, autofocus) and what ends it early. Common things
/// (the filter, the exposure, how many frames) are at the top so that an imaging block is set in three fields. Every change goes into the session at once.
/// </summary>
public sealed partial class BlockDrawerViewModel : ObservableObject, IUnreadableFields
{
    private readonly SessionEditorViewModel _owner;
    private readonly Guid _id;
    private readonly ImagingBindingId? _setup;
    private readonly Rig? _rig;
    private bool _loading = true;
    private List<string> _problems = [];
    private SequenceBlock _block;

    internal BlockDrawerViewModel(SessionEditorViewModel owner, SequenceBlock block, ImagingBindingId? setup)
    {
        _owner = owner;
        _id = block.Id;
        _setup = setup;
        _block = block;
        _rig = owner.ResolvedSetup(setup);
        Until = new ConditionTogglesViewModel(Edited);
        Limits = new ConditionTogglesViewModel(Edited);
        Until.Load(block.Repeat.Until);
        Limits.Load(block.Limits);

        NameText = block.Name ?? string.Empty;
        IsOn = block.Enabled;
        var filterName = owner.FilterNameOf(setup);
        Filters = owner.FiltersOf(_rig);
        HasFilterWheel = _rig?.FilterWheelId is not null;
        SelectedFilter = Filters.FirstOrDefault(f => f.Slot == block.FirstFilter?.Slot) ?? Filters[0];
        ExposureText = block.FirstExposure is { } exposure ? FieldParse.Text(exposure.Seconds) : string.Empty;
        HasExposure = block.FirstExposure is not null;
        RepeatCountOn = block.Repeat.Count is not null;
        RepeatCountText = FieldParse.Text(block.Repeat.Count ?? 10);

        CanDither = _rig?.MountId is not null && _rig.GuiderId is not null;
        CanFocus = _rig?.FocuserId is not null;
        var dither = block.Automation.Dither;
        DitherOn = dither is not null;
        DitherEveryText = FieldParse.Text(dither?.EveryFrames ?? owner.GuidingDefaults.DitherEveryNFrames);
        var settle = dither?.Settings ?? new DitherSettings(owner.GuidingDefaults.DitherAmplitudePixels, owner.GuidingDefaults.SettleThresholdPixels, owner.GuidingDefaults.SettleStableSeconds, owner.GuidingDefaults.SettleTimeoutSeconds);
        DitherAmplitudeText = FieldParse.Text(settle.AmplitudePixels);
        DitherThresholdText = FieldParse.Text(settle.SettleThresholdPixels);
        DitherStableText = FieldParse.Text(settle.SettleStableSeconds);
        DitherTimeoutText = FieldParse.Text(settle.SettleTimeoutSeconds);

        var focus = block.Automation.Focus is { IsActive: true } f ? f : null;
        FocusOn = focus is not null;
        FocusAtStart = focus?.AtBlockStart ?? true;
        FocusEveryOn = focus is { EveryMinutes: > 0 };
        FocusEveryText = FieldParse.Text(focus is { EveryMinutes: > 0 } ? focus.EveryMinutes : 60);
        FocusAfterFilter = focus?.AfterFilterChange ?? false;
        var settings = focus?.Settings ?? new FocusSettings(owner.AutofocusDefaults.ExposureSeconds, owner.AutofocusDefaults.StepSize, owner.AutofocusDefaults.SampleCount);
        FocusExposureText = FieldParse.Text(settings.ExposureSeconds);
        FocusStepText = FieldParse.Text(settings.StepSize);
        FocusSamplesText = FieldParse.Text(settings.SampleCount);

        foreach (var row in owner.RowsFor(block, filterName))
        {
            Actions.Add(row);
        }

        _loading = false;
    }

    public Guid Id => _id;

    /// <summary>What an autofocus of this block means for the other setups on its mount; empty with one setup, or a setup with no mount.</summary>
    public string FocusMountText => !_owner.IsMultiSetup || _rig?.MountId is null ? string.Empty
        : _owner.HoldMountStable ? "While it focuses, the mount is held still: the other setups on this mount wait for it."
        : "The other setups on this mount keep exposing while it focuses. Change this in Settings, Autofocus.";

    public bool HasFocusMountText => FocusMountText.Length > 0;

    /// <summary>The block opens as a heading: "Block 2", or the name the user gave it.</summary>
    public string Heading => "Block";

    [ObservableProperty]
    public partial string NameText { get; set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    // ---- the common things

    public IReadOnlyList<FilterChoice> Filters { get; }

    /// <summary>The setup has a filter wheel: the filter can be chosen.</summary>
    public bool HasFilterWheel { get; }

    [ObservableProperty]
    public partial FilterChoice SelectedFilter { get; set; }

    public bool HasExposure { get; }

    [ObservableProperty]
    public partial string ExposureText { get; set; }

    // ---- the actions

    public ObservableCollection<ActionRowViewModel> Actions { get; } = [];

    // ---- repeat

    /// <summary>The block repeats a number of times; off, only the conditions end it.</summary>
    [ObservableProperty]
    public partial bool RepeatCountOn { get; set; }

    [ObservableProperty]
    public partial string RepeatCountText { get; set; }

    /// <summary>The block repeats until any of these: after the exposure that is running.</summary>
    public ConditionTogglesViewModel Until { get; }

    // ---- automation

    public bool CanDither { get; }

    public bool CanFocus { get; }

    [ObservableProperty]
    public partial bool DitherOn { get; set; }

    [ObservableProperty]
    public partial string DitherEveryText { get; set; }

    [ObservableProperty]
    public partial string DitherAmplitudeText { get; set; }

    [ObservableProperty]
    public partial string DitherThresholdText { get; set; }

    [ObservableProperty]
    public partial string DitherStableText { get; set; }

    [ObservableProperty]
    public partial string DitherTimeoutText { get; set; }

    [ObservableProperty]
    public partial bool FocusOn { get; set; }

    [ObservableProperty]
    public partial bool FocusAtStart { get; set; }

    [ObservableProperty]
    public partial bool FocusEveryOn { get; set; }

    [ObservableProperty]
    public partial string FocusEveryText { get; set; }

    [ObservableProperty]
    public partial bool FocusAfterFilter { get; set; }

    [ObservableProperty]
    public partial string FocusExposureText { get; set; }

    [ObservableProperty]
    public partial string FocusStepText { get; set; }

    [ObservableProperty]
    public partial string FocusSamplesText { get; set; }

    // ---- limits

    /// <summary>What ends this block early: its own limits; the limits of the target are separate.</summary>
    public ConditionTogglesViewModel Limits { get; }

    // ---- problems

    public string ProblemText => string.Join(" ", _problems.Concat(Until.Problems).Concat(Limits.Problems).Append(_owner.ProblemTextOf(_id)).Where(p => p.Length > 0));

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasUnreadableFields => _problems.Count > 0 || Until.Problems.Count > 0 || Limits.Problems.Count > 0;

    // ---- changes

    partial void OnNameTextChanged(string value) => Edited();

    partial void OnIsOnChanged(bool value) => Edited();

    partial void OnExposureTextChanged(string value) => Edited();

    partial void OnRepeatCountOnChanged(bool value) => Edited();

    partial void OnRepeatCountTextChanged(string value) => Edited();

    partial void OnDitherOnChanged(bool value) => Edited();

    partial void OnDitherEveryTextChanged(string value) => Edited();

    partial void OnDitherAmplitudeTextChanged(string value) => Edited();

    partial void OnDitherThresholdTextChanged(string value) => Edited();

    partial void OnDitherStableTextChanged(string value) => Edited();

    partial void OnDitherTimeoutTextChanged(string value) => Edited();

    partial void OnFocusOnChanged(bool value) => Edited();

    partial void OnFocusAtStartChanged(bool value) => Edited();

    partial void OnFocusEveryOnChanged(bool value) => Edited();

    partial void OnFocusEveryTextChanged(string value) => Edited();

    partial void OnFocusAfterFilterChanged(bool value) => Edited();

    partial void OnFocusExposureTextChanged(string value) => Edited();

    partial void OnFocusStepTextChanged(string value) => Edited();

    partial void OnFocusSamplesTextChanged(string value) => Edited();

    // The filter changes the actions of the block (a Set Filter comes or goes), so the list of actions is made again.
    partial void OnSelectedFilterChanged(FilterChoice value)
    {
        if (_loading)
        {
            return;
        }

        _owner.EditBlock(_id, b =>
        {
            var actions = b.Actions.ToList();
            var index = actions.FindIndex(a => a is SetFilterAction);
            if (value.Slot is { } slot)
            {
                if (index >= 0)
                {
                    actions[index] = ((SetFilterAction)actions[index]) with { Slot = slot };
                }
                else
                {
                    actions.Insert(0, new SetFilterAction(Guid.NewGuid(), slot));
                }
            }
            else if (index >= 0)
            {
                actions.RemoveAt(index);
            }

            return b with { Actions = actions };
        }, rebuildDrawer: true);
    }

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        var name = string.IsNullOrWhiteSpace(NameText) ? null : NameText.Trim();
        var current = _block.FirstExposure?.Seconds ?? 0;
        var seconds = HasExposure ? FieldParse.Number(ExposureText, "The exposure", "a number of seconds", problems, current) : current;
        var count = RepeatCountOn ? FieldParse.Whole(RepeatCountText, "The number of frames", problems, _block.Repeat.Count ?? 1) : (int?)null;
        var until = Until.BuildStop();
        var limits = Limits.BuildStop();

        DitherAutomation? dither = null;
        if (DitherOn)
        {
            dither = new DitherAutomation(
                FieldParse.Whole(DitherEveryText, "Dither every N exposures", problems, 3),
                new DitherSettings(
                    FieldParse.Number(DitherAmplitudeText, "The dither amplitude", "a number of pixels", problems, 1.5),
                    FieldParse.Number(DitherThresholdText, "The settle threshold", "a number of pixels", problems, 0.5),
                    FieldParse.Number(DitherStableText, "The settle time", "a number of seconds", problems, 1),
                    FieldParse.Number(DitherTimeoutText, "The settle timeout", "a number of seconds", problems, 10)));
        }

        FocusAutomation? focus = null;
        if (FocusOn)
        {
            focus = new FocusAutomation(
                FocusAtStart, FocusEveryOn ? Math.Max(0, FieldParse.Number(FocusEveryText, "The autofocus interval", "a number of minutes", problems, 60)) : 0, FocusAfterFilter,
                new FocusSettings(
                    FieldParse.Number(FocusExposureText, "The autofocus exposure", "a number of seconds", problems, 1),
                    FieldParse.Whole(FocusStepText, "The autofocus step size", problems, 400),
                    FieldParse.Whole(FocusSamplesText, "The autofocus samples", problems, 7)));
        }

        _problems = problems;
        _block = _block with { Name = name, Enabled = IsOn, Repeat = new RepeatRule(count, until), Automation = new BlockAutomation(dither, focus), Limits = limits };
        _owner.EditBlock(_id, b => b with
        {
            Name = name,
            Enabled = IsOn,
            Repeat = new RepeatRule(count, until),
            Automation = new BlockAutomation(dither, focus),
            Limits = limits,
            Actions = b.Actions.Select(a => a is ExposureAction e && ReferenceEquals(e, b.FirstExposure) ? e with { Seconds = seconds } : a).ToList(),
        });
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasUnreadableFields));
    }

    // ---- commands

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddAction() => _owner.Library.Open(ActionOwner.BlockOf(_id));

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Duplicate() => _owner.DuplicateBlock(_id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveBlock(_id);

    [RelayCommand]
    private void Close() => _owner.ClearSelection();

    private bool IsEditable => _owner.IsEditable;
}
