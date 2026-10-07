using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Rigs;
using Sidera.Desktop.Settings;

namespace Sidera.Desktop.Sessions;

/// <summary>
/// What a new target, block or session starts with: what the setup can do and the defaults of the application propose (Settings), never a change to what exists. A one-camera user without a mount
/// gets a target without a slew; with a mount, a focuser and a guider the target comes with the preparation that such a setup needs.
/// </summary>
public static class SessionTemplates
{
    /// <summary>A target with its preparation and one sequence with one imaging block, as the setup in context can run them.</summary>
    /// <param name="setups">The setups that can image now; the first one with a mount is the one the preparation is for.</param>
    /// <param name="laneSetup">The imaging path of the first sequence; <c>null</c> when there is one setup to image with.</param>
    public static SessionTarget NewTarget(
        string name, double raHours, double decDegrees, double? rotationDegrees, IReadOnlyList<Rig> setups, ImagingBindingId? laneSetup, double exposureSeconds, AutofocusDefaults focus, GuidingDefaults guiding)
    {
        var rig = setups.OrderByDescending(r => r.MountId is not null).FirstOrDefault();
        var preparation = new List<SessionAction>();
        if (rig?.MountId is not null)
        {
            preparation.Add(rotationDegrees is not null && rig.RotatorId is not null
                ? new CenterAndRotateAction(Guid.NewGuid())
                : new SlewAndCenterAction(Guid.NewGuid()));
        }

        if (rig?.FocuserId is not null)
        {
            preparation.Add(new AutofocusAction(Guid.NewGuid(), new FocusSettings(focus.ExposureSeconds, focus.StepSize, focus.SampleCount)));
        }

        if (rig?.GuiderId is not null && guiding.StartBeforeImaging)
        {
            preparation.Add(new StartGuidingAction(Guid.NewGuid()));
        }

        var block = NewBlock(rig, exposureSeconds, focus, guiding);
        return new SessionTarget(Guid.NewGuid(), name, raHours, decDegrees, rotationDegrees, true, preparation, [new SetupLane(Guid.NewGuid(), laneSetup, [block])], []);
    }

    /// <summary>An imaging block: an exposure, repeated; with the automation that the defaults propose for what the setup has.</summary>
    public static SequenceBlock NewBlock(Rig? rig, double exposureSeconds, AutofocusDefaults focus, GuidingDefaults guiding, int frames = 10, int? filterSlot = null)
    {
        var block = SequenceBlock.Imaging(null, filterSlot, exposureSeconds, frames);
        var dither = guiding.DitherByDefault && rig?.MountId is not null && rig.GuiderId is not null
            ? new DitherAutomation(guiding.DitherEveryNFrames, new DitherSettings(guiding.DitherAmplitudePixels, guiding.SettleThresholdPixels, guiding.SettleStableSeconds, guiding.SettleTimeoutSeconds))
            : null;
        var autofocus = focus.PolicyEnabled && rig?.FocuserId is not null
            ? new FocusAutomation(focus.PolicyAtStart, focus.PolicyIntervalMinutes, focus.PolicyAfterFilterChange, new FocusSettings(focus.ExposureSeconds, focus.StepSize, focus.SampleCount))
            : null;
        return block with { Automation = new BlockAutomation(dither, autofocus is { IsActive: true } ? autofocus : null) };
    }

    /// <summary>What a session ends with by default: the guiding stops when the settings say so and a setup is guided.</summary>
    public static IReadOnlyList<SessionAction> DefaultEnd(IReadOnlyList<Rig> setups, GuidingDefaults guiding) =>
        guiding.StopWhenDone && setups.Any(r => r.GuiderId is not null) ? [new StopGuidingAction(Guid.NewGuid())] : [];
}
