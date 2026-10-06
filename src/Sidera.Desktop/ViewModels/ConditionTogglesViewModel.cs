using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The conditions of a block, a wait or a target as the user edits them: a few switches with their values, not a list of expressions. Start conditions are "target altitude above" and
/// "darkness" (all that are on must hold); stop conditions are "target altitude below", "dawn", "time" and "duration" (any that is on stops). Whatever a saved workflow holds that has no
/// switch (a Sun altitude of its own, an exact instant) is kept as it is and written back, so editing a switch never loses a condition. One time zone for the times of day: the one the condition
/// was made with, and for a new one the zone of this computer, which the UI says.
/// </summary>
public sealed partial class ConditionTogglesViewModel : ObservableObject
{
    public static IReadOnlyList<string> TwilightNames { get; } = ["Civil", "Nautical", "Astronomical"];

    private readonly Action _changed;
    private List<WorkflowCondition> _extras = [];
    private string? _zone;
    private bool _loading;
    private double _altitudeAbove = 30, _altitudeBelow = 25, _hours = 4;
    private TimeOnly _time = new(4, 30);

    public ConditionTogglesViewModel(Action changed) => _changed = changed;

    // ---- start

    [ObservableProperty]
    public partial bool AltitudeAboveOn { get; set; }

    [ObservableProperty]
    public partial string AltitudeAboveText { get; set; } = "30";

    [ObservableProperty]
    public partial bool DarknessOn { get; set; }

    [ObservableProperty]
    public partial string DarknessKind { get; set; } = "Astronomical";

    // ---- stop

    [ObservableProperty]
    public partial bool AltitudeBelowOn { get; set; }

    [ObservableProperty]
    public partial string AltitudeBelowText { get; set; } = "25";

    [ObservableProperty]
    public partial bool DawnOn { get; set; }

    [ObservableProperty]
    public partial string DawnKind { get; set; } = "Astronomical";

    [ObservableProperty]
    public partial bool TimeOn { get; set; }

    [ObservableProperty]
    public partial string TimeText { get; set; } = "04:30";

    [ObservableProperty]
    public partial bool DurationOn { get; set; }

    [ObservableProperty]
    public partial string DurationText { get; set; } = "4";

    /// <summary>What is wrong with the numbers that were typed; empty when they all read.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>The conditions that are kept but have no switch, in words; empty when there are none.</summary>
    public string ExtrasText => _extras.Count == 0 ? string.Empty : "Also: " + string.Join(", ", _extras.Select(c => c.Summary));

    public bool HasExtras => _extras.Count > 0;

    /// <summary>The zone the times of day are read in, for the label.</summary>
    public string ZoneText => (_zone is null ? TimeZoneInfo.Local : Find(_zone) ?? TimeZoneInfo.Local).StandardName;

    private static TimeZoneInfo? Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Reads conditions into the switches (without that being an edit).</summary>
    public void Load(IReadOnlyList<WorkflowCondition> conditions)
    {
        _loading = true;
        try
        {
            AltitudeAboveOn = DarknessOn = AltitudeBelowOn = DawnOn = TimeOn = DurationOn = false;
            _extras = [];
            _zone = null;
            foreach (var condition in conditions)
            {
                switch (condition)
                {
                    case TargetAltitudeCondition { Direction: ThresholdDirection.Above } above when !AltitudeAboveOn:
                        AltitudeAboveOn = true;
                        AltitudeAboveText = Format(_altitudeAbove = above.Degrees);
                        break;
                    case TargetAltitudeCondition { Direction: ThresholdDirection.Below } below when !AltitudeBelowOn:
                        AltitudeBelowOn = true;
                        AltitudeBelowText = Format(_altitudeBelow = below.Degrees);
                        break;
                    case TwilightCondition { Event: TwilightEvent.Dusk } dusk when !DarknessOn:
                        DarknessOn = true;
                        DarknessKind = TwilightNames[(int)dusk.Twilight];
                        break;
                    case TwilightCondition { Event: TwilightEvent.Dawn } dawn when !DawnOn:
                        DawnOn = true;
                        DawnKind = TwilightNames[(int)dawn.Twilight];
                        break;
                    case TimeCondition { TimeOfDay: { } timeOfDay } time when !TimeOn:
                        TimeOn = true;
                        _time = timeOfDay;
                        _zone = time.TimeZoneId;
                        TimeText = timeOfDay.ToString("HH':'mm", CultureInfo.InvariantCulture);
                        break;
                    case DurationCondition duration when !DurationOn:
                        DurationOn = true;
                        DurationText = Format(_hours = duration.Duration.TotalHours);
                        break;
                    default:
                        _extras.Add(condition);
                        break;
                }
            }

            Problems = [];
            OnPropertyChanged(nameof(ExtrasText));
            OnPropertyChanged(nameof(HasExtras));
            OnPropertyChanged(nameof(ZoneText));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The start conditions as they are now: all that are on (and what was kept).</summary>
    public IReadOnlyList<WorkflowCondition> BuildStart()
    {
        var problems = new List<string>();
        var list = new List<WorkflowCondition>();
        if (AltitudeAboveOn)
        {
            list.Add(new TargetAltitudeCondition(Number(AltitudeAboveText, "The altitude", "a number of degrees", problems, ref _altitudeAbove), ThresholdDirection.Above));
        }

        if (DarknessOn)
        {
            list.Add(new TwilightCondition(TwilightOf(DarknessKind), TwilightEvent.Dusk));
        }

        Problems = problems;
        return [.. list, .. _extras];
    }

    /// <summary>The stop conditions as they are now: any that is on (and what was kept).</summary>
    public IReadOnlyList<WorkflowCondition> BuildStop()
    {
        var problems = new List<string>();
        var list = new List<WorkflowCondition>();
        if (AltitudeBelowOn)
        {
            list.Add(new TargetAltitudeCondition(Number(AltitudeBelowText, "The altitude", "a number of degrees", problems, ref _altitudeBelow), ThresholdDirection.Below));
        }

        if (DawnOn)
        {
            list.Add(new TwilightCondition(TwilightOf(DawnKind), TwilightEvent.Dawn));
        }

        if (TimeOn)
        {
            list.Add(TimeCondition.AtLocalTime(ReadTime(problems), _zone ?? TimeZoneInfo.Local.Id));
        }

        if (DurationOn)
        {
            var hours = Number(DurationText, "The duration", "a number of hours", problems, ref _hours);
            list.Add(new DurationCondition(TimeSpan.FromHours(Math.Max(0, hours))));
        }

        Problems = problems;
        return [.. list, .. _extras];
    }

    /// <summary>The time of day of a Wait Until (the field of the time condition), as a condition.</summary>
    public WorkflowCondition BuildTime()
    {
        var problems = new List<string>();
        var condition = TimeCondition.AtLocalTime(ReadTime(problems), _zone ?? TimeZoneInfo.Local.Id);
        Problems = problems;
        return condition;
    }

    private TimeOnly ReadTime(List<string> problems)
    {
        if (TimeOnly.TryParseExact(TimeText?.Trim(), ["HH:mm", "H:mm", "HH.mm", "H.mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return _time = parsed;
        }

        problems.Add("The time must be hours and minutes, like 04:30.");
        return _time;
    }

    private static Twilight TwilightOf(string? name) => name switch
    {
        "Civil" => Twilight.Civil,
        "Nautical" => Twilight.Nautical,
        _ => Twilight.Astronomical,
    };

    private static double Number(string? text, string label, string expected, List<string> problems, ref double lastGood)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return lastGood = value;
        }

        problems.Add($"{label} must be {expected}.");
        return lastGood;
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    // ---- every switch and value is an edit of the owner

    partial void OnAltitudeAboveOnChanged(bool value) => Edited();
    partial void OnAltitudeAboveTextChanged(string value) => Edited();
    partial void OnDarknessOnChanged(bool value) => Edited();
    partial void OnDarknessKindChanged(string value) => Edited();
    partial void OnAltitudeBelowOnChanged(bool value) => Edited();
    partial void OnAltitudeBelowTextChanged(string value) => Edited();
    partial void OnDawnOnChanged(bool value) => Edited();
    partial void OnDawnKindChanged(string value) => Edited();
    partial void OnTimeOnChanged(bool value) => Edited();
    partial void OnTimeTextChanged(string value) => Edited();
    partial void OnDurationOnChanged(bool value) => Edited();
    partial void OnDurationTextChanged(string value) => Edited();

    private void Edited()
    {
        if (!_loading)
        {
            _changed();
        }
    }

    /// <summary>Start conditions in a few words for a row: "altitude ≥ 30° and astronomical darkness".</summary>
    public static string SummarizeStart(IReadOnlyList<WorkflowCondition> conditions) =>
        string.Join(" and ", conditions.Select(Short));

    /// <summary>Stop conditions in a few words for a row: "40 frames or dawn or altitude &lt; 25°". The frames are the block's own and come first when given.</summary>
    public static string SummarizeStop(IReadOnlyList<WorkflowCondition> conditions, int? frames = null) =>
        string.Join(" or ", (frames is { } n ? [$"{n} frames"] : Array.Empty<string>()).Concat(conditions.Select(Short)));

    private static string Short(WorkflowCondition condition) => condition switch
    {
        TargetAltitudeCondition { Direction: ThresholdDirection.Above } a => string.Create(CultureInfo.InvariantCulture, $"altitude ≥ {a.Degrees:0.#}°"),
        TargetAltitudeCondition b => string.Create(CultureInfo.InvariantCulture, $"altitude < {b.Degrees:0.#}°"),
        TwilightCondition { Event: TwilightEvent.Dusk } d => $"{d.Twilight.Title()} darkness",
        TwilightCondition w => $"{w.Twilight.Title()} dawn",
        TimeCondition { TimeOfDay: { } t } => $"{t:HH\\:mm}",
        DurationCondition d => $"{d.Duration.TotalHours:0.##} h",
        _ => condition.Summary,
    };
}
