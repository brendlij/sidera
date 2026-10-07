using System;
using System.Collections.Generic;
using Sidera.Core.Conditions;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Sessions;

/// <summary>What an action does. The kinds are part of the saved format (see the session document); never derive a stored name from a class name.</summary>
public enum SessionActionKind
{
    Exposure,
    SetFilter,
    Autofocus,
    MoveFocuser,
    Wait,
    WaitUntil,
    StartGuiding,
    StopGuiding,
    DitherNow,
    Slew,
    SlewAndCenter,
    PlateSolve,
    CenterAndRotate,
    SyncMount,
    CoolCamera,
    WarmCamera,
    Park,
    Unpark,
    SetTracking,
}

/// <summary>The groups the action library shows.</summary>
public enum ActionCategory
{
    Camera,
    FilterWheel,
    Focus,
    Guiding,
    Mount,
    Astrometry,
    Control,
}

/// <summary>Where an action may be put: a block (it runs inside the imaging of one setup), the preparation of a target, or the start or end of the session.</summary>
[Flags]
public enum ActionScope
{
    None = 0,
    Block = 1,
    Preparation = 2,
    Session = 4,
    Everywhere = Block | Preparation | Session,
}

/// <summary>The focus settings of an autofocus: the exposure, the step between samples and how many samples.</summary>
public sealed record FocusSettings(double ExposureSeconds, int StepSize, int SampleCount)
{
    public static FocusSettings Default { get; } = new(1, 400, 7);
}

/// <summary>How a dither is made and when it counts as settled.</summary>
public sealed record DitherSettings(double AmplitudePixels, double SettleThresholdPixels, double SettleStableSeconds, double SettleTimeoutSeconds)
{
    public static DitherSettings Default { get; } = new(1.5, 0.5, 1, 10);
}

/// <summary>
/// One thing the sequence does, in the words of the person at the telescope. An action never names a device: it works with the imaging setup of the lane it is in (or, in the preparation of a target and at
/// the start and end of the session, with the setups that are imaged). <see cref="Setup"/> is the Advanced override that says another setup; it is <c>null</c> in every normal action.
/// </summary>
public abstract record SessionAction(Guid Id)
{
    /// <summary>An action that is off stays where it is and is left out of the sequence.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The imaging setup this action works with instead of the one that is in context: the Advanced override. <c>null</c> for nearly every action.</summary>
    public ImagingBindingId? Setup { get; init; }

    public abstract SessionActionKind Kind { get; }
}

public sealed record ExposureAction(Guid Id, double Seconds) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Exposure;
}

public sealed record SetFilterAction(Guid Id, int Slot) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.SetFilter;
}

public sealed record AutofocusAction(Guid Id, FocusSettings Settings) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Autofocus;
}

public sealed record MoveFocuserAction(Guid Id, int Position) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.MoveFocuser;
}

public sealed record WaitAction(Guid Id, double Seconds) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Wait;
}

public sealed record WaitUntilAction(Guid Id, IReadOnlyList<WorkflowCondition> Conditions) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.WaitUntil;
}

public sealed record StartGuidingAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.StartGuiding;
}

public sealed record StopGuidingAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.StopGuiding;
}

public sealed record DitherNowAction(Guid Id, DitherSettings Settings) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.DitherNow;
}

/// <summary>Slews to the coordinates of the target it is in.</summary>
public sealed record SlewAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Slew;
}

/// <summary>Slews to the target and centers it by plate solving; with a rotation on the target and a rotator it also rotates.</summary>
public sealed record SlewAndCenterAction(Guid Id, double ToleranceArcseconds = 60, int MaxAttempts = 5, double SolveExposureSeconds = 5) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.SlewAndCenter;
}

public sealed record PlateSolveAction(Guid Id, double ExposureSeconds = 5) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.PlateSolve;
}

/// <summary>Centers the target and turns the rotator to the rotation of the target.</summary>
public sealed record CenterAndRotateAction(Guid Id, double ToleranceArcseconds = 60, int MaxAttempts = 5, double SolveExposureSeconds = 5) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.CenterAndRotate;
}

/// <summary>Syncs the mount to the last plate-solved position. Never done by itself.</summary>
public sealed record SyncMountAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.SyncMount;
}

/// <summary>Cools the camera to a temperature, in steps over <see cref="RampMinutes"/> so that the sensor is not shocked.</summary>
public sealed record CoolCameraAction(Guid Id, double TargetCelsius = -10, double RampMinutes = 5) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.CoolCamera;
}

/// <summary>Warms the camera back up over <see cref="RampMinutes"/> and switches the cooler off.</summary>
public sealed record WarmCameraAction(Guid Id, double RampMinutes = 10) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.WarmCamera;
}

public sealed record ParkAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Park;
}

public sealed record UnparkAction(Guid Id) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.Unpark;
}

public sealed record SetTrackingAction(Guid Id, bool On = true) : SessionAction(Id)
{
    public override SessionActionKind Kind => SessionActionKind.SetTracking;
}

/// <summary>What the action library knows about an action: its name, where it is found, where it may go, and how to make a new one with sensible values.</summary>
public sealed record ActionInfo(SessionActionKind Kind, string Title, ActionCategory Category, string Description, ActionScope Scope, Func<Guid, SessionAction> Create);

/// <summary>The actions of the library, in the order it shows them. One list: the library, the editor and the tests all read it, so an action that exists is offered where it can be used and nowhere else.</summary>
public static class ActionCatalog
{
    private static readonly ActionInfo[] All =
    [
        new(SessionActionKind.Exposure, "Exposure", ActionCategory.Camera, "Takes a frame.", ActionScope.Block, id => new ExposureAction(id, 300)),
        new(SessionActionKind.CoolCamera, "Cool Camera", ActionCategory.Camera, "Cools the camera to a temperature, gently.", ActionScope.Session | ActionScope.Preparation, id => new CoolCameraAction(id)),
        new(SessionActionKind.WarmCamera, "Warm Camera", ActionCategory.Camera, "Warms the camera up and switches the cooler off.", ActionScope.Session | ActionScope.Preparation, id => new WarmCameraAction(id)),

        new(SessionActionKind.SetFilter, "Set Filter", ActionCategory.FilterWheel, "Turns the filter wheel to a filter.", ActionScope.Block, id => new SetFilterAction(id, 0)),

        new(SessionActionKind.Autofocus, "Autofocus", ActionCategory.Focus, "Focuses now.", ActionScope.Everywhere, id => new AutofocusAction(id, FocusSettings.Default)),
        new(SessionActionKind.MoveFocuser, "Move Focuser", ActionCategory.Focus, "Moves the focuser to a position.", ActionScope.Block | ActionScope.Preparation, id => new MoveFocuserAction(id, 0)),

        new(SessionActionKind.StartGuiding, "Start Guiding", ActionCategory.Guiding, "Starts guiding and waits until it is calibrated and settled.", ActionScope.Session | ActionScope.Preparation, id => new StartGuidingAction(id)),
        new(SessionActionKind.StopGuiding, "Stop Guiding", ActionCategory.Guiding, "Stops guiding.", ActionScope.Session | ActionScope.Preparation, id => new StopGuidingAction(id)),
        new(SessionActionKind.DitherNow, "Dither Now", ActionCategory.Guiding, "Dithers once, now.", ActionScope.Preparation, id => new DitherNowAction(id, DitherSettings.Default)),

        new(SessionActionKind.Slew, "Slew", ActionCategory.Mount, "Slews to the coordinates of the target.", ActionScope.Preparation, id => new SlewAction(id)),
        new(SessionActionKind.SlewAndCenter, "Slew & Center", ActionCategory.Mount, "Slews to the target and centers it by plate solving.", ActionScope.Preparation, id => new SlewAndCenterAction(id)),
        new(SessionActionKind.Park, "Park", ActionCategory.Mount, "Parks the mount.", ActionScope.Session | ActionScope.Preparation, id => new ParkAction(id)),
        new(SessionActionKind.Unpark, "Unpark", ActionCategory.Mount, "Unparks the mount.", ActionScope.Session | ActionScope.Preparation, id => new UnparkAction(id)),
        new(SessionActionKind.SetTracking, "Set Tracking", ActionCategory.Mount, "Switches tracking on or off.", ActionScope.Session | ActionScope.Preparation, id => new SetTrackingAction(id)),

        new(SessionActionKind.PlateSolve, "Plate Solve", ActionCategory.Astrometry, "Solves the field with a frame.", ActionScope.Preparation, id => new PlateSolveAction(id)),
        new(SessionActionKind.CenterAndRotate, "Center & Rotate", ActionCategory.Astrometry, "Centers the target and turns the rotator to the rotation of the target.", ActionScope.Preparation, id => new CenterAndRotateAction(id)),
        new(SessionActionKind.SyncMount, "Sync Mount", ActionCategory.Astrometry, "Syncs the mount to the last solved position.", ActionScope.Preparation, id => new SyncMountAction(id)),

        new(SessionActionKind.Wait, "Wait", ActionCategory.Control, "Waits for a number of seconds.", ActionScope.Everywhere, id => new WaitAction(id, 60)),
        new(SessionActionKind.WaitUntil, "Wait Until", ActionCategory.Control, "Waits until the sky, the time or the target says go.", ActionScope.Everywhere, id => new WaitUntilAction(id, [])),
    ];

    public static IReadOnlyList<ActionInfo> Entries => All;

    public static ActionInfo Of(SessionActionKind kind) => System.Array.Find(All, a => a.Kind == kind) ?? throw new ArgumentOutOfRangeException(nameof(kind));

    /// <summary>The actions that may go in a place, in the library's order.</summary>
    public static IEnumerable<ActionInfo> For(ActionScope place) => System.Array.FindAll(All, a => (a.Scope & place) != 0);
}
