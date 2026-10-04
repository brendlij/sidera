using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Astra.Core.Focusing;
using Astra.Core.Rigs;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// What the autofocus of one rig is doing, or did last: the lines the sequencer shows for it, and the samples taken so
/// far (position and HFR) for a later chart. It only says what the run reported; nothing here is estimated, and there is
/// no percentage because the run does not know in advance how many samples a second pattern will take.
/// </summary>
public sealed partial class AutofocusStatusViewModel : ObservableObject
{
    /// <summary>What the line says for an autofocus that is a step the user wrote.</summary>
    public const string ManualOrigin = "Manual sequence step";

    /// <summary>What the line says for an autofocus the policy of the track asked for.</summary>
    public static string AutomaticOrigin(AutofocusOrigin origin) => origin switch
    {
        AutofocusOrigin.TrackStart => "Automatic · track start",
        AutofocusOrigin.AfterFilterChange => "Automatic · after filter change",
        _ => "Automatic",
    };

    private readonly List<FocusMeasurement> _measurements = [];
    private IReadOnlyList<string> _body = [];

    public AutofocusStatusViewModel(RigId rigId, string rigName)
    {
        RigId = rigId;
        Title = $"AUTOFOCUS · {Short(rigName)}";
        Lines = [];
    }

    public RigId RigId { get; }

    /// <summary>For example "AUTOFOCUS · MAIN".</summary>
    public string Title { get; }

    /// <summary>
    /// Why this autofocus runs: "Manual sequence step", "Automatic · track start" or "Automatic · after filter change";
    /// <c>null</c> when that is not known.
    /// </summary>
    [ObservableProperty]
    public partial string? Origin { get; private set; }

    /// <summary>The lines under the title: the origin, then "Sample 4 / 7", "Position 20100", "HFR 2.14 px", or the result.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Lines { get; private set; }

    /// <summary>The run is going on (it has neither completed nor stopped).</summary>
    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>The run found focus.</summary>
    [ObservableProperty]
    public partial bool IsCompleted { get; private set; }

    /// <summary>The position focus was found at, once completed.</summary>
    [ObservableProperty]
    public partial int? BestPosition { get; private set; }

    /// <summary>The HFR at the best position, once completed.</summary>
    [ObservableProperty]
    public partial double? BestHfr { get; private set; }

    /// <summary>
    /// What the run is doing in one line, from what it reported: "Sample 4 / 7 · HFR 2.11 px", "Fitting focus curve",
    /// "Focused at 19970 · HFR 1.82 px". Empty until the run said something.
    /// </summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The samples of the run so far, in the order they were taken.</summary>
    public IReadOnlyList<FocusMeasurement> Measurements => _measurements;

    /// <summary>Says why the autofocus that starts now runs.</summary>
    public void SetOrigin(string? origin)
    {
        Origin = origin;
        Lines = Compose();
    }

    /// <summary>Takes what the run reported.</summary>
    public void Apply(AutofocusProgress progress)
    {
        switch (progress.Phase)
        {
            case AutofocusPhase.Measuring:
                IsActive = true;
                if (progress.SampleIndex == 0)
                {
                    if (progress.Attempt == 1)
                    {
                        _measurements.Clear();
                        IsCompleted = false;
                        BestPosition = null;
                        BestHfr = null;
                    }

                    Show([Invariant($"Sampling {progress.SampleCount} focus positions"), .. Pass(progress)]);
                    Summarize(Invariant($"Sampling {progress.SampleCount} positions"));
                }
                else
                {
                    _measurements.Add(new FocusMeasurement(progress.Position!.Value, progress.Hfr!.Value));
                    Show(
                    [
                        Invariant($"Sample {progress.SampleIndex} / {progress.SampleCount}"),
                        Invariant($"Position {progress.Position}"),
                        Invariant($"HFR {progress.Hfr:0.00} px"),
                        .. Pass(progress),
                    ]);
                    Summarize(Invariant($"Sample {progress.SampleIndex} / {progress.SampleCount} · HFR {progress.Hfr:0.00} px"));
                }

                break;
            case AutofocusPhase.Fitting:
                Show(["Fitting focus curve", .. Pass(progress)]);
                Summarize("Fitting focus curve");
                break;
            case AutofocusPhase.Moving:
                Show(["Moving to best focus", Invariant($"{progress.BestPosition} steps")]);
                Summarize("Moving to best focus");
                break;
            case AutofocusPhase.Verifying:
                Show(["Checking the focus", Invariant($"Position {progress.BestPosition}")]);
                Summarize("Checking the focus");
                break;
            case AutofocusPhase.Completed:
                IsActive = false;
                IsCompleted = true;
                BestPosition = progress.BestPosition;
                BestHfr = progress.BestHfr;
                Show(
                [
                    "Best focus",
                    Invariant($"{progress.BestPosition} steps"),
                    "HFR",
                    Invariant($"{progress.BestHfr:0.00} px"),
                ]);
                Summarize(Invariant($"Focused at {progress.BestPosition} · HFR {progress.BestHfr:0.00} px"));
                break;
            case AutofocusPhase.Stopped:
                IsActive = false;
                Show(["Autofocus stopped"]);
                Summarize("Autofocus stopped");
                break;
        }
    }

    private void Show(IReadOnlyList<string> body)
    {
        _body = body;
        Lines = Compose();
    }

    private void Summarize(string summary) => Summary = summary;

    private IReadOnlyList<string> Compose() => Origin is null ? _body : [Origin, .. _body];

    // Only a second pattern says so: the first one needs no remark.
    private static IEnumerable<string> Pass(AutofocusProgress progress) =>
        progress.Attempt > 1 ? [Invariant($"Pass {progress.Attempt}")] : [];

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // "Main Rig" is "MAIN".
    private static string Short(string rigName)
    {
        var name = rigName.EndsWith(" Rig", StringComparison.OrdinalIgnoreCase) ? rigName[..^4] : rigName;
        return name.ToUpperInvariant();
    }
}
