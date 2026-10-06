using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop;

/// <summary>
/// The values a new step starts with, and the draft the editor starts with. The equipment defaults are the demo
/// devices where they are registered, otherwise the first device of the kind, otherwise none; the timings are those
/// of <see cref="DemoOptions"/>.
/// </summary>
public sealed record SequenceDraftDefaults
{
    /// <summary>Where the demo slews to (the Orion Nebula).</summary>
    public const double TargetRightAscensionHours = 5.588;
    public const double TargetDeclinationDegrees = -5.39;

    public DeviceId? CameraId { get; init; }
    public DeviceId? MountId { get; init; }
    public DeviceId? GuiderId { get; init; }
    public DeviceId? FocuserId { get; init; }
    public DeviceId? FilterWheelId { get; init; }

    /// <summary>The position a new focuser move starts with: where the default focuser stands.</summary>
    public int FocuserPosition { get; init; } = SimulatedFocuser.DefaultStartPosition;

    /// <summary>The slot a new filter change starts with.</summary>
    public int FilterSlotIndex { get; init; }

    /// <summary>The exposure, step size and number of samples a new autofocus starts with.</summary>
    public double AutofocusExposureSeconds { get; init; } = 1;
    public int AutofocusStepSize { get; init; } = 400;
    public int AutofocusSampleCount { get; init; } = 7;

    /// <summary>The rig a new top-level autofocus starts with; <c>null</c> when none is known.</summary>
    public RigId? AutofocusRigId { get; init; }

    public double ExposureSeconds { get; init; } = 2;
    public double DelaySeconds { get; init; } = 2;
    public double DitherAmplitudePixels { get; init; } = 1.5;
    public double SettleThresholdPixels { get; init; } = 0.5;
    public double SettleStableSeconds { get; init; } = 1;
    public double SettleTimeoutSeconds { get; init; } = 10;

    public static SequenceDraftDefaults From(DemoOptions options, DeviceRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        var devices = registry.GetAll();

        // The device a new step starts with: the demo's own when it is there, else the one device that can be meant. With several and nothing to say which, none: the step asks for a choice instead
        // of picking the first one.
        DeviceId? Pick<T>(DeviceId preferred) where T : class, IDevice =>
            devices.OfType<T>().FirstOrDefault(d => d.Id == preferred)?.Id
            ?? DeviceResolver.Resolve<T>(registry, null, null, string.Empty).Device;

        var focuserId = Pick<IFocuser>(DemoSetup.MainFocuserId);
        var focuser = focuserId is { } id ? devices.OfType<IFocuser>().FirstOrDefault(f => f.Id == id) : null;

        return new SequenceDraftDefaults
        {
            CameraId = Pick<ICamera>(DemoSetup.MainCameraId),
            MountId = Pick<IMount>(DemoSetup.MountId),
            GuiderId = Pick<IGuider>(DemoSetup.GuiderId),
            FocuserId = focuserId,
            FilterWheelId = Pick<IFilterWheel>(DemoSetup.MainFilterWheelId),
            FocuserPosition = focuser?.Position ?? SimulatedFocuser.DefaultStartPosition,
            ExposureSeconds = options.SequenceExposure.TotalSeconds,
            DelaySeconds = options.SequenceWait.TotalSeconds,
            DitherAmplitudePixels = options.DitherAmplitudePixels,
            SettleThresholdPixels = options.SettleThresholdPixels,
            SettleStableSeconds = options.SettleStableDuration.TotalSeconds,
            SettleTimeoutSeconds = options.SettleTimeout.TotalSeconds,
        };
    }

    /// <summary>The default number of repetitions of a new Repeat.</summary>
    public int RepeatCount { get; init; } = 2;

    /// <summary>A new step of <paramref name="kind"/> with a new id and these defaults; a Repeat starts empty.</summary>
    public SequenceStepDraft Create(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.Repeat => new RepeatStepDraft(Guid.NewGuid(), RepeatCount, []),
        SequenceStepKind.MultiRig => new MultiRigStepDraft(Guid.NewGuid(), []),
        _ => CreateLeaf(kind),
    };

    /// <summary>A new leaf step of <paramref name="kind"/>, as it is added to the sequence or to a Repeat.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a leaf step.</exception>
    public LeafStepDraft CreateLeaf(SequenceStepKind kind)
    {
        var id = Guid.NewGuid();
        return kind switch
        {
            SequenceStepKind.Exposure => new ExposureStepDraft(id, CameraId, ExposureSeconds),
            SequenceStepKind.RigExposure => new RigExposureStepDraft(id, ExposureSeconds),
            SequenceStepKind.Delay => new DelayStepDraft(id, DelaySeconds),
            SequenceStepKind.Slew => new SlewStepDraft(id, MountId, TargetRightAscensionHours, TargetDeclinationDegrees),
            SequenceStepKind.StartGuiding => new StartGuidingStepDraft(id, GuiderId),
            SequenceStepKind.StopGuiding => new StopGuidingStepDraft(id, GuiderId),
            SequenceStepKind.MoveFocuser => new MoveFocuserStepDraft(id, FocuserId, FocuserPosition),
            SequenceStepKind.ChangeFilter => new ChangeFilterStepDraft(id, FilterWheelId, FilterSlotIndex),
            SequenceStepKind.RigMoveFocuser => new RigMoveFocuserStepDraft(id, FocuserPosition),
            SequenceStepKind.RigChangeFilter => new RigChangeFilterStepDraft(id, FilterSlotIndex),
            SequenceStepKind.SlewAndCenter => new SlewAndCenterStepDraft(id, null, AutofocusRigId, TargetRightAscensionHours, TargetDeclinationDegrees, 60, 5, 5),
            SequenceStepKind.RotateToAngle => new RotateToAngleStepDraft(id, AutofocusRigId, 0),
            SequenceStepKind.RotateAndVerify => new RotateAndVerifyStepDraft(
                id, AutofocusRigId, 0, Sidera.Runtime.Astrometry.RotationService.DefaultToleranceDegrees, Sidera.Runtime.Astrometry.RotationService.DefaultMaxAttempts, 5),
            SequenceStepKind.CenterAndRotate => new CenterAndRotateStepDraft(
                id, null, AutofocusRigId, TargetRightAscensionHours, TargetDeclinationDegrees, 60, 5, 0,
                Sidera.Runtime.Astrometry.RotationService.DefaultToleranceDegrees, Sidera.Runtime.Astrometry.RotationService.DefaultMaxAttempts,
                Sidera.Runtime.Astrometry.RotationService.DefaultMaxRounds, 5),
            SequenceStepKind.SyncMountToSolved => new SyncMountStepDraft(id, MountId),
            SequenceStepKind.PlateSolve => new PlateSolveStepDraft(id, AutofocusRigId, 5),
            SequenceStepKind.Autofocus => new AutofocusStepDraft(
                id, AutofocusRigId, AutofocusExposureSeconds, AutofocusStepSize, AutofocusSampleCount),
            SequenceStepKind.RigAutofocus => new RigAutofocusStepDraft(
                id, AutofocusExposureSeconds, AutofocusStepSize, AutofocusSampleCount),
            SequenceStepKind.Dither => new DitherStepDraft(
                id, GuiderId, MountId, CameraId, DitherAmplitudePixels,
                SettleThresholdPixels, SettleStableSeconds, SettleTimeoutSeconds),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a leaf step."),
        };
    }

    /// <summary>
    /// The sequence the editor starts with: the demo as a linear sequence. Three exposures with a dither between
    /// each, between starting and stopping guiding after a slew to the target. The demo's second branch, which
    /// dithered in parallel with the exposures, has no linear form and is not part of it.
    /// </summary>
    public IReadOnlyList<SequenceStepDraft> InitialSteps() =>
    [
        Create(SequenceStepKind.StartGuiding),
        Create(SequenceStepKind.Slew),
        Create(SequenceStepKind.Exposure),
        Create(SequenceStepKind.Dither),
        Create(SequenceStepKind.Exposure),
        Create(SequenceStepKind.Dither),
        Create(SequenceStepKind.Exposure),
        Create(SequenceStepKind.StopGuiding),
    ];
}
