using System;
using System.Globalization;
using System.Linq;
using Sidera.Core.Conditions;

namespace Sidera.Desktop.Sessions;

/// <summary>An action in the words of the person who placed it: its title, and what it is set to in a few characters ("300 s", "Ha", "every 60 min"). Never a device and never an id.</summary>
public static class ActionSummary
{
    /// <param name="filterName">The name of the filter in a slot of the wheel of the setup in context, when it is known.</param>
    public static string Of(SessionAction action, Func<int, string?>? filterName = null) => action switch
    {
        ExposureAction e => Seconds(e.Seconds),
        SetFilterAction f => filterName?.Invoke(f.Slot) is { Length: > 0 } name ? name : $"slot {f.Slot + 1}",
        AutofocusAction a => string.Create(CultureInfo.InvariantCulture, $"{a.Settings.ExposureSeconds:0.##} s × {a.Settings.SampleCount} samples"),
        MoveFocuserAction m => $"to {m.Position}",
        WaitAction w => Seconds(w.Seconds),
        WaitUntilAction u => u.Conditions.Count == 0 ? "choose what to wait for" : string.Join(" and ", u.Conditions.Select(BlockSummary.Brief)),
        DitherNowAction d => string.Create(CultureInfo.InvariantCulture, $"{d.Settings.AmplitudePixels:0.##} px"),
        SlewAndCenterAction c => string.Create(CultureInfo.InvariantCulture, $"{c.ToleranceArcseconds:0.##}\" · up to {c.MaxAttempts} attempts"),
        CenterAndRotateAction r => string.Create(CultureInfo.InvariantCulture, $"{r.ToleranceArcseconds:0.##}\" · up to {r.MaxAttempts} attempts"),
        PlateSolveAction p => Seconds(p.ExposureSeconds),
        CoolCameraAction c => string.Create(CultureInfo.InvariantCulture, $"to {c.TargetCelsius:0.#} °C over {c.RampMinutes:0.#} min"),
        WarmCameraAction w => string.Create(CultureInfo.InvariantCulture, $"over {w.RampMinutes:0.#} min"),
        SetTrackingAction t => t.On ? "on" : "off",
        _ => string.Empty,
    };

    public static string Title(SessionAction action) => ActionCatalog.Of(action.Kind).Title;

    private static string Seconds(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s");
}
