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
    private readonly List<FocusMeasurement> _measurements = [];

    public AutofocusStatusViewModel(RigId rigId, string rigName)
    {
        RigId = rigId;
        Title = $"AUTOFOCUS · {Short(rigName)}";
        Lines = [];
    }

    public RigId RigId { get; }

    /// <summary>For example "AUTOFOCUS · MAIN".</summary>
    public string Title { get; }

    /// <summary>The lines under the title: "Sample 4 / 7", "Position 20100", "HFR 2.14 px", or the result.</summary>
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

    /// <summary>The samples of the run so far, in the order they were taken.</summary>
    public IReadOnlyList<FocusMeasurement> Measurements => _measurements;

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
                    }

                    Lines = [Invariant($"Sampling {progress.SampleCount} focus positions"), .. Pass(progress)];
                }
                else
                {
                    _measurements.Add(new FocusMeasurement(progress.Position!.Value, progress.Hfr!.Value));
                    Lines =
                    [
                        Invariant($"Sample {progress.SampleIndex} / {progress.SampleCount}"),
                        Invariant($"Position {progress.Position}"),
                        Invariant($"HFR {progress.Hfr:0.00} px"),
                        .. Pass(progress),
                    ];
                }

                break;
            case AutofocusPhase.Fitting:
                Lines = ["Fitting focus curve", .. Pass(progress)];
                break;
            case AutofocusPhase.Moving:
                Lines = ["Moving to best focus", Invariant($"{progress.BestPosition} steps")];
                break;
            case AutofocusPhase.Verifying:
                Lines = ["Checking the focus", Invariant($"Position {progress.BestPosition}")];
                break;
            case AutofocusPhase.Completed:
                IsActive = false;
                IsCompleted = true;
                BestPosition = progress.BestPosition;
                BestHfr = progress.BestHfr;
                Lines =
                [
                    "Best focus",
                    Invariant($"{progress.BestPosition} steps"),
                    "HFR",
                    Invariant($"{progress.BestHfr:0.00} px"),
                ];
                break;
            case AutofocusPhase.Stopped:
                IsActive = false;
                Lines = ["Autofocus stopped"];
                break;
        }
    }

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
