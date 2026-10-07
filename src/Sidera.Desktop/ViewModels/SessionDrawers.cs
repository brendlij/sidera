using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Mounts;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>Reads what was typed: a number, a whole number; a field that does not read is told, and the last good value stays.</summary>
internal static class FieldParse
{
    public static double Number(string? text, string label, string expected, List<string> problems, double fallback)
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

    public static int Whole(string? text, string label, List<string> problems, int fallback)
    {
        var trimmed = text?.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        problems.Add($"{label} must be a whole number.");
        return fallback;
    }

    public static string Text(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A target in the drawer: its name, where it points, its rotation, what ends it. The coordinates are the target's; an action of the target never repeats them.</summary>
public sealed partial class TargetDrawerViewModel : ObservableObject, IUnreadableFields
{
    private readonly SessionEditorViewModel _owner;
    private readonly Guid _id;
    private readonly bool _loading = true;
    private List<string> _problems = [];

    internal TargetDrawerViewModel(SessionEditorViewModel owner, SessionTarget target)
    {
        _owner = owner;
        _id = target.Id;
        NameText = target.Name;
        RaText = FieldParse.Text(target.RightAscensionHours);
        DecText = FieldParse.Text(target.DeclinationDegrees);
        RotationText = target.RotationDegrees is { } r ? FieldParse.Text(r) : string.Empty;
        Limits = new ConditionTogglesViewModel(Edited);
        Limits.Load(target.Limits);
        ShownCoordinates = Show(target.RightAscensionHours, target.DeclinationDegrees);
        _target = target;
        _loading = false;
    }

    private SessionTarget _target;

    public string Heading => "Target";

    [ObservableProperty]
    public partial string NameText { get; set; }

    /// <summary>Right ascension, in hours: a decimal number or hours minutes seconds ("05:35:17").</summary>
    [ObservableProperty]
    public partial string RaText { get; set; }

    /// <summary>Declination, in degrees: a decimal number or degrees minutes seconds ("-05:23:28").</summary>
    [ObservableProperty]
    public partial string DecText { get; set; }

    /// <summary>The rotation of the frame in degrees; empty when it does not matter.</summary>
    [ObservableProperty]
    public partial string RotationText { get; set; }

    /// <summary>The coordinates the way an atlas writes them.</summary>
    [ObservableProperty]
    public partial string ShownCoordinates { get; private set; }

    /// <summary>What ends the target for all its sequences at once: after the exposures that run, the end of the session follows.</summary>
    public ConditionTogglesViewModel Limits { get; }

    public string ProblemText => string.Join(" ", _problems.Concat(Limits.Problems));

    public bool HasProblem => ProblemText.Length > 0;

    public bool HasUnreadableFields => _problems.Count > 0 || Limits.Problems.Count > 0;

    partial void OnNameTextChanged(string value) => Edited();

    partial void OnRaTextChanged(string value) => Edited();

    partial void OnDecTextChanged(string value) => Edited();

    partial void OnRotationTextChanged(string value) => Edited();

    private static string Show(double ra, double dec) => $"RA {SkyFormat.Ra(ra)} · Dec {SkyFormat.Dec(dec)}";

    private void Edited()
    {
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        var ra = SkyFormat.TryParseHours(RaText, out var hours) && hours is >= 0 and < 24 ? hours : Fail(problems, "The right ascension must be hours from 0 to 24 (5.588 or 05:35:17).", _target.RightAscensionHours);
        var dec = SkyFormat.TryParseDegrees(DecText, out var degrees) && degrees is >= -90 and <= 90 ? degrees : Fail(problems, "The declination must be degrees from -90 to 90 (-5.39 or -05:23:28).", _target.DeclinationDegrees);
        double? rotation = string.IsNullOrWhiteSpace(RotationText) ? null : FieldParse.Number(RotationText, "The rotation", "a number of degrees", problems, _target.RotationDegrees ?? 0);
        var limits = Limits.BuildStop();
        _problems = problems;
        var name = string.IsNullOrWhiteSpace(NameText) ? _target.Name : NameText.Trim();
        _target = _target with { Name = name, RightAscensionHours = ra, DeclinationDegrees = dec, RotationDegrees = rotation, Limits = limits };
        ShownCoordinates = Show(ra, dec);
        _owner.EditTarget(_id, t => t with { Name = name, RightAscensionHours = ra, DeclinationDegrees = dec, RotationDegrees = rotation, Limits = limits });
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasUnreadableFields));
    }

    private static double Fail(List<string> problems, string message, double fallback)
    {
        problems.Add(message);
        return fallback;
    }

    [RelayCommand]
    private void Close() => _owner.ClearSelection();
}

/// <summary>The meridian flip of the session in the drawer: it follows the application's settings, or it is custom for this session. One flip for the session, run for every mount.</summary>
public sealed partial class FlipEditorViewModel : ObservableObject, IUnreadableFields
{
    private readonly SessionEditorViewModel _owner;
    private readonly bool _loading = true;
    private MeridianFlipSettings _flip;
    private List<string> _problems = [];

    internal FlipEditorViewModel(SessionEditorViewModel owner, SessionAutomation automation)
    {
        _owner = owner;
        UsesDefaults = automation.UsesDefaultFlip;
        _flip = automation.Flip ?? owner.ApplicationFlip;
        Read();
        _loading = false;
    }

    public string Heading => "Meridian flip";

    /// <summary>The session follows the application's meridian flip settings (Settings); nothing of them is copied into it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(FieldsVisible), nameof(Summary))]
    public partial bool UsesDefaults { get; private set; }

    public bool IsCustom => !UsesDefaults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FieldsVisible), nameof(Summary))]
    public partial bool Enabled { get; set; }

    /// <summary>The fields of the session's own flip are shown: it has its own flip, and it is on.</summary>
    public bool FieldsVisible => Enabled && !UsesDefaults;

    [ObservableProperty]
    public partial string PauseBeforeText { get; set; } = "5";

    [ObservableProperty]
    public partial string AfterText { get; set; } = "2";

    [ObservableProperty]
    public partial string LatestText { get; set; } = "15";

    /// <summary>An exposure that is running when the flip comes due is let to finish. Always on in this version: the box is shown, not changeable.</summary>
    public bool FinishCurrentExposure => true;

    [ObservableProperty]
    public partial bool StopGuiding { get; set; } = true;

    [ObservableProperty]
    public partial bool Recenter { get; set; } = true;

    [ObservableProperty]
    public partial bool Rotation { get; set; } = true;

    [ObservableProperty]
    public partial bool Autofocus { get; set; }

    [ObservableProperty]
    public partial bool RestartGuiding { get; set; } = true;

    [ObservableProperty]
    public partial bool Dither { get; set; }

    [ObservableProperty]
    public partial string PauseAfterText { get; set; } = "0";

    [ObservableProperty]
    public partial string AttemptsText { get; set; } = "2";

    /// <summary>A failed flip holds the setups of its mount until the user retries or aborts (on), or ends the session (off).</summary>
    [ObservableProperty]
    public partial bool PauseOnFailure { get; set; } = true;

    [ObservableProperty]
    public partial string ToleranceText { get; set; } = "60";

    [ObservableProperty]
    public partial string CenterAttemptsText { get; set; } = "5";

    /// <summary>The flip in a sentence: "Hold new exposures 5 min before the meridian, flip 2 min after it, at the latest 15 min after."</summary>
    public string Summary => Sentence(UsesDefaults ? _owner.ApplicationFlip : _flip with { Enabled = Enabled });

    /// <summary>"Using defaults · Hold −5m · Flip +2m · Recenter", or that the defaults are off.</summary>
    public string DefaultsSummary
    {
        get
        {
            var line = _owner.FlipLine(SessionAutomation.Defaults);
            return line.StartsWith("Off", StringComparison.Ordinal) ? "Using defaults · off (turn it on in Settings → Meridian Flip, or customize it for this session)" : "Using defaults · " + line.Replace("Enabled · ", string.Empty, StringComparison.Ordinal);
        }
    }

    public string ProblemText => string.Join(" ", _problems);

    public bool HasProblem => _problems.Count > 0;

    public bool HasUnreadableFields => !UsesDefaults && _problems.Count > 0;

    private static string Sentence(MeridianFlipSettings s) => s.Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"Hold new exposures {s.PauseBeforeMeridianMinutes:0.#} min before the meridian, flip {s.FlipAfterMeridianMinutes:0.#} min after it, at the latest {s.LatestAllowedFlipMinutes:0.#} min after.")
        : "Off";

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void UseDefaultSettings()
    {
        if (UsesDefaults)
        {
            return;
        }

        UsesDefaults = true;
        _problems = [];
        _owner.EditAutomation(SessionAutomation.Defaults);
        OnPropertyChanged(nameof(DefaultsSummary));
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Customize()
    {
        if (!UsesDefaults)
        {
            return;
        }

        _flip = _owner.ApplicationFlip;
        var wasLoading = _reading;
        _reading = true;
        try
        {
            Read();
        }
        finally
        {
            _reading = wasLoading;
        }

        UsesDefaults = false;
        _owner.EditAutomation(new SessionAutomation(_flip));
    }

    [RelayCommand]
    private void Close() => _owner.ClearSelection();

    private bool IsEditable => _owner.IsEditable;

    private bool _reading;

    private void Read()
    {
        var wasReading = _reading;
        _reading = true;
        try
        {
            Enabled = _flip.Enabled;
            PauseBeforeText = FieldParse.Text(_flip.PauseBeforeMeridianMinutes);
            AfterText = FieldParse.Text(_flip.FlipAfterMeridianMinutes);
            LatestText = FieldParse.Text(_flip.LatestAllowedFlipMinutes);
            StopGuiding = _flip.StopGuidingBeforeFlip;
            Recenter = _flip.RecenterAfterFlip;
            Rotation = _flip.VerifyRotationAfterFlip;
            Autofocus = _flip.AutofocusAfterFlip;
            RestartGuiding = _flip.RestartGuidingAfterFlip;
            Dither = _flip.DitherAfterFlip;
            PauseAfterText = FieldParse.Text(_flip.PauseAfterFlipMinutes);
            AttemptsText = FieldParse.Text(_flip.MaxFlipAttempts);
            PauseOnFailure = _flip.FailureBehavior == MeridianFlipFailureBehavior.PauseSession;
            ToleranceText = FieldParse.Text(_flip.CenteringToleranceArcseconds);
            CenterAttemptsText = FieldParse.Text(_flip.MaxCenteringAttempts);
        }
        finally
        {
            _reading = wasReading;
        }
    }

    partial void OnEnabledChanged(bool value) => Edited();

    partial void OnPauseBeforeTextChanged(string value) => Edited();

    partial void OnAfterTextChanged(string value) => Edited();

    partial void OnLatestTextChanged(string value) => Edited();

    partial void OnStopGuidingChanged(bool value) => Edited();

    partial void OnRecenterChanged(bool value) => Edited();

    partial void OnRotationChanged(bool value) => Edited();

    partial void OnAutofocusChanged(bool value) => Edited();

    partial void OnRestartGuidingChanged(bool value) => Edited();

    partial void OnDitherChanged(bool value) => Edited();

    partial void OnPauseAfterTextChanged(string value) => Edited();

    partial void OnAttemptsTextChanged(string value) => Edited();

    partial void OnPauseOnFailureChanged(bool value) => Edited();

    partial void OnToleranceTextChanged(string value) => Edited();

    partial void OnCenterAttemptsTextChanged(string value) => Edited();

    private void Edited()
    {
        OnPropertyChanged(nameof(Summary));
        if (_loading || _reading || UsesDefaults)
        {
            return; // the fields are not shown; what is in them is not the session's
        }

        var problems = new List<string>();
        _flip = _flip with
        {
            Enabled = Enabled,
            PauseBeforeMeridianMinutes = FieldParse.Number(PauseBeforeText, "The pause before the meridian", "a number of minutes", problems, _flip.PauseBeforeMeridianMinutes),
            FlipAfterMeridianMinutes = FieldParse.Number(AfterText, "The flip after the meridian", "a number of minutes", problems, _flip.FlipAfterMeridianMinutes),
            LatestAllowedFlipMinutes = FieldParse.Number(LatestText, "The latest allowed flip", "a number of minutes", problems, _flip.LatestAllowedFlipMinutes),
            StopGuidingBeforeFlip = StopGuiding,
            RecenterAfterFlip = Recenter,
            VerifyRotationAfterFlip = Rotation,
            AutofocusAfterFlip = Autofocus,
            RestartGuidingAfterFlip = RestartGuiding,
            DitherAfterFlip = Dither,
            PauseAfterFlipMinutes = FieldParse.Number(PauseAfterText, "The pause after the flip", "a number of minutes", problems, _flip.PauseAfterFlipMinutes),
            MaxFlipAttempts = FieldParse.Whole(AttemptsText, "The flip attempts", problems, _flip.MaxFlipAttempts),
            FailureBehavior = PauseOnFailure ? MeridianFlipFailureBehavior.PauseSession : MeridianFlipFailureBehavior.AbortSession,
            CenteringToleranceArcseconds = FieldParse.Number(ToleranceText, "The centering tolerance", "a number of arcseconds", problems, _flip.CenteringToleranceArcseconds),
            MaxCenteringAttempts = FieldParse.Whole(CenterAttemptsText, "The centering attempts", problems, _flip.MaxCenteringAttempts),
        };
        _problems = problems;
        _owner.EditAutomation(new SessionAutomation(_flip));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(HasUnreadableFields));
    }
}
