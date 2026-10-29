using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Focusing;
using Sidera.Core.Guiding;
using Sidera.Core.Imaging;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>What a mount group's flip is made of: the mount, its setups, the target it keeps pointing at, and what to do after the flip.</summary>
/// <param name="PointingRig">The setup whose camera plate-solves for the group, whose rotator is verified; chosen by the workflow, never at random.</param>
/// <param name="Setups">Every setup on the mount that takes part in the flip (they all hold at safe points for it).</param>
/// <param name="AutofocusOptions">The measurements of an autofocus after the flip.</param>
/// <param name="Settle">What settled guiding means after the guider started again.</param>
/// <param name="OnAutofocused">Called after an autofocus of the flip, so that the autofocus policies of the setups start their intervals again.</param>
public sealed record MeridianFlipPlan(
    DeviceId MountId,
    MeridianFlipSettings Settings,
    CelestialCoordinates Target,
    string? TargetName,
    double? DesiredRotationDegrees,
    Rig PointingRig,
    IReadOnlyList<Rig> Setups,
    AutofocusOptions AutofocusOptions,
    GuidingSettleOptions Settle,
    double DitherAmplitudePixels,
    Action? OnAutofocused = null);

/// <summary>The services a flip uses; nothing new is built for it. Each is optional where the settings can do without it.</summary>
public sealed record MeridianFlipServices(
    DeviceRegistry Registry,
    PlateSolveService? PlateSolving = null,
    RotationService? Rotation = null,
    IFocusMetricProvider? FocusMetrics = null,
    IEventPublisher? Events = null,
    ILoggerFactory? Loggers = null,
    IAcquisitionDefaultsSource? AcquisitionDefaults = null,
    Func<PlateSolveDefaults>? PlateSolveDefaults = null,
    TimeProvider? Time = null,
    Func<ObservingSite?>? Site = null,
    TimeSpan? PollInterval = null);

/// <summary>What a setup that is about to expose is told.</summary>
public enum MeridianGateDecision
{
    /// <summary>Expose.</summary>
    Clear,

    /// <summary>The exposure would not end before the flip: wait, at safe points, until the flip is due.</summary>
    Wait,

    /// <summary>The flip is due: it happens now, once everybody is at a safe point.</summary>
    FlipDue,

    /// <summary>The latest allowed flip has passed without one.</summary>
    Overdue,

    /// <summary>The hold has begun and the mount is to stand still: guiding and tracking are stopped, once, with every setup at a safe point.</summary>
    Standstill,
}

/// <summary>
/// The meridian flip of one mount, shared by every setup on it (one object for the group in one run). It decides, from the astronomical hour angle of the target, whether a setup may start an
/// exposure (<see cref="Evaluate"/>), and when the flip is due it runs the flip once for everybody as a coordinated operation of the group's coordination group, so that no setup of the mount
/// exposes while the mount moves, and other mount groups are not touched.
/// <para>
/// The flip, in this order: stop the guider (if it guides), slew to the same target and let the driver choose the pier side, check the slew (and the pier side where the driver reports one),
/// plate solve and center, verify the rotation, autofocus, start the guider again and wait until it settles, dither, pause. What is switched off in the settings is left out. Nothing here
/// synchronizes the mount: pointing is verified by plate solving, and a correction is a slew. Attempts and failure follow the settings.
/// </para>
/// </summary>
public sealed class MeridianFlipGroup
{
    private readonly MeridianFlipPlan _plan;
    private readonly MeridianFlipServices _services;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<DeviceId> _wasGuiding = [];
    private readonly List<FlipChild> _children = [];
    private bool _initialized;
    private bool _passed;
    private bool _flipping;
    private bool _forced;
    private bool _standstilling;
    private bool _standstillDone;
    private Task<bool>? _errorHold;
    private PierSide _pierBefore = PierSide.Unknown;
    private bool _sideChanged;
    private CelestialCoordinates? _centeringStart;
    private TaskCompletionSource<bool>? _decision;

    public MeridianFlipGroup(MeridianFlipPlan plan, MeridianFlipServices services)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(services);
        _plan = plan;
        _services = services;
        _logger = services.Loggers?.CreateLogger<MeridianFlipGroup>() ?? NullLogger<MeridianFlipGroup>.Instance;
        BuildChildren();
    }

    public DeviceId MountId => _plan.MountId;

    public MeridianFlipSettings Settings => _plan.Settings;

    public string TargetName => _plan.TargetName ?? "the target";

    /// <summary>How often a setup that waits for the flip looks at the sky again: a second, which is plenty for minutes.</summary>
    public TimeSpan PollInterval => _services.PollInterval ?? TimeSpan.FromSeconds(1);

    /// <summary>The names of the setups that hold for this flip.</summary>
    public IReadOnlyList<string> SetupNames => _plan.Setups.Select(r => r.Name).ToList();

    public MeridianFlipState State { get; private set; } = MeridianFlipState.Monitoring;

    /// <summary>A sentence about what the flip is doing.</summary>
    public string Message { get; private set; } = string.Empty;

    /// <summary>The flip happened, or the target was already past the meridian when imaging began and none is needed.</summary>
    public bool Passed
    {
        get { lock (_gate) { return _passed; } }
    }

    /// <summary>Which attempt of the mount flip and centering is running; 0 before the first.</summary>
    public int Attempt { get; private set; }

    /// <summary>The steps the flip runs, in order, as the runner tracks them. Some are skipped at run time (the guider was not guiding).</summary>
    public IReadOnlyList<ISequenceStep> Children => _children.Select(c => c.Step).ToList();

    private DateTime UtcNow => (_services.Time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    // ---- the sky

    /// <summary>The target's hour angle in minutes (negative before the meridian), from the site and the clock.</summary>
    /// <exception cref="MeridianFlipFailedException">There is no site to compute it for.</exception>
    public double HourAngleMinutes()
    {
        var site = _services.Site?.Invoke()
            ?? (_services.Registry.TryGet(_plan.MountId, out var device) && device is IMountControl { Site: { } mountSite } ? mountSite.ToObservingSite() : null)
            ?? throw new MeridianFlipFailedException("The meridian flip needs the observing site (Settings), or a mount that reports one.");
        return MeridianFlipTiming.HourAngleHoursOfJ2000(_plan.Target.RightAscensionHours, _plan.Target.DeclinationDegrees, UtcNow, site.LongitudeDegrees) * 60;
    }

    /// <summary>
    /// What a setup that wants to start an exposure of <paramref name="exposureSeconds"/> does now. The first time it is asked, a target that is already west of the meridian counts as flipped
    /// (a slew to it chose the side of the pier it is on): the flip is for the crossing that happens during the session.
    /// </summary>
    public async Task<MeridianGateDecision> EvaluateAsync(double exposureSeconds, CancellationToken cancellationToken)
    {
        var minutes = HourAngleMinutes();
        lock (_gate)
        {
            if (_passed)
            {
                return MeridianGateDecision.Clear;
            }

            if (!_initialized)
            {
                _initialized = true;
                if (minutes > 0)
                {
                    _passed = true;
                    Log("MeridianFlipNotNeeded", "Meridian flip not needed: {Target} is {Minutes:0.#} min past the meridian at the start", TargetName, minutes);
                    return MeridianGateDecision.Clear;
                }
            }

            if (_forced)
            {
                return MeridianGateDecision.FlipDue;
            }

            // Another setup of this mount failed near the meridian and waits for the user: nobody exposes.
            if (_errorHold is not null)
            {
                return MeridianGateDecision.Wait;
            }
        }

        var phase = MeridianFlipTiming.PhaseOf(_plan.Settings, minutes);
        if (_plan.Settings.StopTrackingWhileHolding && phase == MeridianFlipPhase.Approaching)
        {
            lock (_gate)
            {
                if (!_standstillDone)
                {
                    return MeridianGateDecision.Standstill;
                }
            }
        }

        var decision = phase switch
        {
            MeridianFlipPhase.Overdue => MeridianGateDecision.Overdue,
            MeridianFlipPhase.FlipDue => MeridianGateDecision.FlipDue,
            _ => MeridianFlipTiming.CanStartExposure(_plan.Settings, minutes, exposureSeconds) ? MeridianGateDecision.Clear : MeridianGateDecision.Wait,
        };

        if (phase == MeridianFlipPhase.Approaching && State == MeridianFlipState.Monitoring)
        {
            Log("MeridianFlipApproaching", "Meridian flip approaching: {Target} crosses the meridian in {Minutes:0.#} min", TargetName, -minutes);
            await SetStateAsync(MeridianFlipState.Approaching, $"{TargetName} crosses the meridian in {FormatMinutes(-minutes)}. New exposures only start when they fit before the flip.", minutes / 60, cancellationToken);
        }

        if (decision == MeridianGateDecision.Wait && State is MeridianFlipState.Monitoring or MeridianFlipState.Approaching)
        {
            Log("MeridianFlipHoldStarted", "Meridian flip hold: an exposure of {Seconds} s would not end before the flip, hour angle {Minutes:0.#} min", exposureSeconds, minutes);
            await SetStateAsync(
                MeridianFlipState.HoldingForSafePoint,
                _standstillDone
                    ? $"The mount stands still before the meridian: tracking and guiding are stopped. The flip is due in {FormatMinutes(_plan.Settings.FlipAfterMeridianMinutes - minutes)}."
                    : $"Holding new exposures until the flip ({FormatMinutes(_plan.Settings.FlipAfterMeridianMinutes - minutes)} to go).",
                minutes / 60, cancellationToken);
        }

        return decision;
    }

    // ---- the standstill

    /// <summary>
    /// The hold has begun and the settings say that the mount stands still: the first setup that asks stops guiding and tracking for the whole mount group, at a point where every setup is safe (no exposure
    /// runs), and the others wait until it is done. Tracking comes back with the flip (which starts it before the slew), guiding with the guiding that the flip restarts.
    /// </summary>
    public async Task StandstillAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        bool requester;
        lock (_gate)
        {
            requester = !_standstilling && !_standstillDone;
            if (requester)
            {
                _standstilling = true;
            }
        }

        if (!requester)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_standstillDone || !_standstilling)
                    {
                        return;
                    }
                }

                await context.ReachSafePointAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            }
        }

        try
        {
            await context.ExecuteWhenSafeAsync(ct => StopForTheHoldAsync(ct), cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _standstilling = false;
            }
        }
    }

    // Stops what is guiding (remembering it, so that it is started again) and the tracking of the mount. Null when it is done, otherwise a sentence about what could not be stopped.
    private async Task<string?> StopGuidingAndTrackingAsync(CancellationToken cancellationToken)
    {
        string? problem = null;
        foreach (var guiderId in _plan.Setups.Select(r => r.GuiderId).OfType<DeviceId>().Distinct())
        {
            try
            {
                if (_services.Registry.TryGet(guiderId, out var guiderDevice) && guiderDevice is IGuider { ConnectionState: DeviceConnectionState.Connected } guider
                    && guider.GuidingState is GuidingState.Guiding or GuidingState.Dithering or GuidingState.Starting)
                {
                    lock (_gate)
                    {
                        _wasGuiding.Add(guiderId);
                    }

                    await guider.StopGuidingAsync(cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Guiding of {Guider} could not be stopped", guiderId);
                problem = "Guiding could not be stopped: stop it yourself.";
            }
        }

        if (_services.Registry.TryGet(_plan.MountId, out var device) && device is IMountControl { ConnectionState: DeviceConnectionState.Connected } control)
        {
            try
            {
                await control.SetTrackingAsync(false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Tracking of {Mount} could not be stopped", MountId);
                problem = (problem is null ? string.Empty : problem + " ") + "The tracking of the mount could not be stopped: stop it yourself.";
            }
        }

        return problem;
    }

    private async Task StopForTheHoldAsync(CancellationToken cancellationToken)
    {
        var minutes = HourAngleMinutes();
        if (await StopGuidingAndTrackingAsync(cancellationToken) is { } problem)
        {
            throw new MeridianFlipFailedException($"Guiding and tracking could not be stopped for the hold: {problem}");
        }

        lock (_gate)
        {
            _standstillDone = true;
        }

        Log("MeridianFlipStandstill", "The mount {Mount} stands still before the meridian: guiding and tracking stopped, hour angle {Minutes:0.#} min", MountId, minutes);
        await SetStateAsync(
            MeridianFlipState.HoldingForSafePoint,
            $"The mount stands still before the meridian: tracking and guiding are stopped. The flip is due in {FormatMinutes(_plan.Settings.FlipAfterMeridianMinutes - minutes)}.",
            minutes / 60, cancellationToken);
    }

    // ---- the flip

    /// <summary>
    /// Runs the flip for the whole mount group: the first setup that asks becomes the requester and runs it as a coordinated operation (every other setup is held at its safe point meanwhile); the
    /// others wait at their safe points until it is over. A setup that arrives after it is over finds nothing to do.
    /// </summary>
    public async Task FlipAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        bool requester;
        lock (_gate)
        {
            requester = !_flipping && !_passed;
            if (requester)
            {
                _flipping = true;
            }
        }

        if (!requester)
        {
            while (!Passed)
            {
                await context.ReachSafePointAsync(cancellationToken);
                if (Passed)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
            }

            return;
        }

        try
        {
            await context.ExecuteWhenSafeAsync(ct => RunFlipAsync(context, ct), cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _flipping = false;
            }
        }
    }

    private async Task RunFlipAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var settings = _plan.Settings;
        var minutes = HourAngleMinutes();
        Log("MeridianFlipSafePointReached", "Meridian flip: every setup of {Mount} is at a safe point ({Setups}), hour angle {Minutes:0.#} min", MountId, string.Join(", ", SetupNames), minutes);

        if (MeridianFlipTiming.PhaseOf(settings, minutes) == MeridianFlipPhase.Overdue && !_forced)
        {
            await FailAsync($"The latest allowed flip ({settings.LatestAllowedFlipMinutes:0.#} min after the meridian) has passed without a flip.", null, cancellationToken);
        }

        Log("MeridianFlipStarted", "Meridian flip started: mount {Mount}, setups {Setups}, target {Target}, hour angle {Minutes:0.#} min", MountId, string.Join(", ", SetupNames), TargetName, minutes);
        await RunPhaseAsync(FlipPhase.Before, cancellationToken, context, retries: false);
        await RunPhaseAsync(FlipPhase.Mount, cancellationToken, context, retries: true);
        await RunPhaseAsync(FlipPhase.After, cancellationToken, context, retries: false);

        lock (_gate)
        {
            _passed = true;
            _forced = false;
        }

        Log("MeridianFlipCompleted", "Meridian flip completed: mount {Mount}, setups {Setups}, {Seconds:0.#} s", MountId, string.Join(", ", SetupNames), (DateTime.UtcNow - started).TotalSeconds);
        await SetStateAsync(MeridianFlipState.Completed, "The flip is complete. Imaging resumes.", null, CancellationToken.None);
    }

    private async Task RunPhaseAsync(FlipPhase phase, CancellationToken cancellationToken, ISequenceStepContext context, bool retries)
    {
        var attempts = retries ? _plan.Settings.MaxFlipAttempts : 1;
        var attempt = 0;
        while (true)
        {
            attempt++;
            if (retries)
            {
                Attempt = attempt;
            }

            try
            {
                for (var i = 0; i < _children.Count; i++)
                {
                    var child = _children[i];
                    if (child.Phase != phase || !child.ShouldRun())
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await SetStateAsync(child.State, child.Message(), SafeHourAngleHours(), cancellationToken);
                    await context.ExecuteChildAsync(child.Step, i, _children.Count, cancellationToken);
                    await child.Completed();
                }

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                Log("MeridianFlipAttemptFailed", "Meridian flip attempt {Attempt} of {Attempts} failed: {Reason}", attempt, attempts, ex.Message);
            }
            catch (Exception ex)
            {
                // Out of attempts: the failure behavior decides; a retry asked for by the user starts this phase again, with new attempts.
                await FailAsync(ex.Message, ex, cancellationToken);
                attempt = 0;
            }
        }
    }

    // Aborts (throws) or holds the group and waits for the user to retry or abort. Returns when the user retries.
    private async Task FailAsync(string reason, Exception? inner, CancellationToken cancellationToken)
    {
        Log("MeridianFlipFailed", "Meridian flip failed: {Reason} (mount {Mount}, setups {Setups})", reason, MountId, string.Join(", ", SetupNames));
        var secured = await SecureMountAsync();
        if (secured is not null)
        {
            reason = reason.TrimEnd() + " " + secured;
        }

        if (_plan.Settings.FailureBehavior == MeridianFlipFailureBehavior.AbortSession)
        {
            await SetStateAsync(MeridianFlipState.Failed, $"The flip failed: {reason}", SafeHourAngleHours(), CancellationToken.None);
            throw new MeridianFlipFailedException($"The meridian flip failed: {reason}", inner);
        }

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _decision = decision;
        }

        await SetStateAsync(MeridianFlipState.Failed, $"The flip failed: {reason} The setups on this mount are held. Retry the flip or abort the session.", SafeHourAngleHours(), CancellationToken.None);
        using var registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
        bool retry;
        try
        {
            retry = await decision.Task;
        }
        finally
        {
            lock (_gate)
            {
                _decision = null;
            }
        }

        lock (_gate)
        {
            _forced = retry;
        }

        if (!retry)
        {
            throw new MeridianFlipFailedException($"The meridian flip failed: {reason}", inner);
        }

        Log("MeridianFlipRetry", "Meridian flip retried by the user (mount {Mount})", MountId);
    }

    // A flip that failed leaves the mount tracking past the meridian. When it did not change sides it stands on the side that it was on before the meridian, and tracking on would take it further into
    // the limits of the mount: tracking is stopped, and that is said. A mount that did change sides stands on the safe side and keeps tracking.
    private async Task<string?> SecureMountAsync()
    {
        if (_sideChanged || !_services.Registry.TryGet(_plan.MountId, out var device) || device is not IMountControl { ConnectionState: DeviceConnectionState.Connected } control
            || control.Telemetry is not { Tracking: true })
        {
            return null;
        }

        try
        {
            await control.SetTrackingAsync(false, CancellationToken.None);
            Log("MeridianFlipTrackingStopped", "Meridian flip failed before the mount changed sides: tracking of {Mount} was stopped for safety", MountId);
            return "The mount did not change sides, so its tracking was stopped.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tracking of {Mount} could not be stopped after the failed flip", MountId);
            return "The mount did not change sides and its tracking could not be stopped: stop it yourself.";
        }
    }

    /// <summary>How close to the meridian (in minutes, on top of the hold) a run that ends without the flip has to be for the tracking to be stopped.</summary>
    public const double EndOfRunMarginMinutes = 10;

    /// <summary>
    /// The run is over, whatever the reason (it finished, failed, or was cancelled) and whatever its end actions did or did not do: a mount that has not flipped and is within the hold of the meridian (or
    /// past it) must not go on tracking, because with nobody to flip it, it would track into its limits, or into the pier. Tracking is stopped, and the returned sentence says so; <c>null</c> when there was
    /// nothing to do (the flip happened, it is not needed, the target is still far from the meridian). It does not depend on the end steps of a session: it is called by whoever ran the sequence.
    /// </summary>
    public async Task<string?> SecureAtRunEndAsync()
    {
        lock (_gate)
        {
            // The flip happened or is not needed, or it is running and knows what to do on failure. A run that never looked at the sky has not begun: nothing was tracked past anything.
            if (_passed || _flipping || !_initialized)
            {
                return null;
            }
        }

        double minutes;
        try
        {
            minutes = HourAngleMinutes();
        }
        catch (MeridianFlipFailedException)
        {
            return null; // no site: the sky cannot be computed
        }

        if (minutes < -(_plan.Settings.PauseBeforeMeridianMinutes + EndOfRunMarginMinutes)
            || !_services.Registry.TryGet(_plan.MountId, out var device) || device is not IMountControl { ConnectionState: DeviceConnectionState.Connected } control)
        {
            return null;
        }

        var where = minutes < 0 ? $"{FormatMinutes(-minutes)} before the meridian" : $"{FormatMinutes(minutes)} past the meridian";
        try
        {
            // No look at what the mount says about its tracking: a mount that reports stale data would be believed. Stopping a mount that does not track is harmless.
            await control.SetTrackingAsync(false, CancellationToken.None);
            Log("MeridianFlipSecuredAtRunEnd", "The run ended {Where} without the flip: tracking of {Mount} was stopped for safety", where, MountId);
            return $"The session ended {where} without the flip, so the tracking of the mount was stopped.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Tracking of {Mount} could not be stopped when the run ended {Where} without the flip", MountId, where);
            return $"The session ended {where} without the flip and the tracking of the mount could not be stopped: stop it yourself.";
        }
    }

    // ---- an error near the meridian

    /// <summary>
    /// A step of the run failed (the guider lost its star, a dither failed, anything). Near the meridian, with the flip still to come, a failure must not leave the mount tracking and must not end the
    /// session: guiding and tracking are stopped, every setup of the mount is held, the error is shown, and the group waits for the user. <c>true</c> when the user retries (tracking and guiding come
    /// back, unless the mount is to stand still until the flip, and the step is run again), <c>false</c> when the user aborts (the failure goes on), <c>null</c> when this is not such a time (far from the
    /// meridian, the flip happened or is running and has its own handling): the failure is not this group's.
    /// </summary>
    public async Task<bool?> HoldAfterErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException or MeridianFlipFailedException)
        {
            return null;
        }

        Task<bool>? running;
        lock (_gate)
        {
            running = _errorHold;
            if (running is null && (_passed || _flipping || !_initialized || _decision is not null))
            {
                return null;
            }
        }

        if (running is not null)
        {
            return await running.WaitAsync(cancellationToken);
        }

        double minutes;
        try
        {
            minutes = HourAngleMinutes();
        }
        catch (MeridianFlipFailedException)
        {
            return null;
        }

        if (minutes < -(_plan.Settings.PauseBeforeMeridianMinutes + EndOfRunMarginMinutes))
        {
            return null;
        }

        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            running = _errorHold;
            if (running is null)
            {
                _errorHold = source.Task;
            }
        }

        if (running is not null)
        {
            return await running.WaitAsync(cancellationToken);
        }

        try
        {
            var retry = await HoldForTheUserAsync(exception, minutes, cancellationToken);
            source.SetResult(retry);
            return retry;
        }
        catch (Exception ex)
        {
            source.SetException(ex);
            _ = source.Task.Exception; // observed here; the others that wait get it too
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _errorHold = null;
            }
        }
    }

    private async Task<bool> HoldForTheUserAsync(Exception exception, double minutes, CancellationToken cancellationToken)
    {
        Log(
            "MeridianErrorHold", "An error {Minutes:0.#} min from the meridian without the flip: {Reason} (mount {Mount}, setups {Setups}); guiding and tracking are stopped and the setups held",
            minutes, exception.Message, MountId, string.Join(", ", SetupNames));
        var problem = await StopGuidingAndTrackingAsync(CancellationToken.None);
        var message = $"Something went wrong near the meridian: {exception.Message.TrimEnd()} " +
            (problem ?? "Guiding and tracking were stopped, and the setups on this mount are held.") + " Retry to go on (tracking and guiding start again), or abort the session.";

        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _decision = decision;
        }

        await SetStateAsync(MeridianFlipState.Failed, message, SafeHourAngleHours(), CancellationToken.None);
        using var registration = cancellationToken.Register(() => decision.TrySetCanceled(cancellationToken));
        bool retry;
        try
        {
            retry = await decision.Task;
        }
        finally
        {
            lock (_gate)
            {
                _decision = null;
            }
        }

        if (!retry)
        {
            return false;
        }

        await ResumeAfterErrorAsync(cancellationToken);
        return true;
    }

    // The user retries. Tracking and guiding come back, unless the hold of the mount (it stands still until the flip) is on: then it stays still, as it would have.
    private async Task ResumeAfterErrorAsync(CancellationToken cancellationToken)
    {
        var minutes = HourAngleMinutes();
        if (_plan.Settings.StopTrackingWhileHolding && minutes >= -_plan.Settings.PauseBeforeMeridianMinutes)
        {
            lock (_gate)
            {
                _standstillDone = true;
            }

            await SetStateAsync(MeridianFlipState.HoldingForSafePoint, "The mount stands still before the meridian until the flip.", minutes / 60, cancellationToken);
            return;
        }

        if (_services.Registry.TryGet(_plan.MountId, out var device) && device is IMountControl { ConnectionState: DeviceConnectionState.Connected } control)
        {
            await control.SetTrackingAsync(true, cancellationToken);
        }

        List<DeviceId> guiders;
        lock (_gate)
        {
            guiders = [.. _wasGuiding];
        }

        foreach (var guiderId in guiders)
        {
            if (_services.Registry.TryGet(guiderId, out var guiderDevice) && guiderDevice is IGuider { ConnectionState: DeviceConnectionState.Connected } guider)
            {
                await guider.StartGuidingAsync(cancellationToken);
                if (guider is IGuidingSettler settler)
                {
                    await settler.SettleAsync(_plan.Settle, cancellationToken);
                }
            }
        }

        lock (_gate)
        {
            _wasGuiding.Clear();
        }

        await SetStateAsync(MeridianFlipState.Approaching, "Going on after the error.", minutes / 60, cancellationToken);
    }

    /// <summary>The flip failed and waits: try it again (the mount is flipped, centered and so on as before).</summary>
    public bool Retry() => Decide(true);

    /// <summary>The flip failed and waits: end the session.</summary>
    public bool Abort() => Decide(false);

    /// <summary>The flip has failed and waits for the user to retry or abort.</summary>
    public bool IsWaitingForDecision
    {
        get { lock (_gate) { return _decision is not null; } }
    }

    private bool Decide(bool retry)
    {
        TaskCompletionSource<bool>? decision;
        lock (_gate)
        {
            decision = _decision;
        }

        return decision?.TrySetResult(retry) ?? false;
    }

    // ---- what the flip is made of

    private enum FlipPhase
    {
        Before,
        Mount,
        After,
    }

    private sealed record FlipChild(FlipPhase Phase, MeridianFlipState State, Func<string> Message, ISequenceStep Step, Func<bool> ShouldRun, Func<Task> Completed);

    private void BuildChildren()
    {
        var settings = _plan.Settings;
        var registry = _services.Registry;
        var guiders = _plan.Setups.Select(r => r.GuiderId).OfType<DeviceId>().Distinct().ToList();

        static Task Nothing() => Task.CompletedTask;
        void Add(FlipPhase phase, MeridianFlipState state, string message, ISequenceStep step, Func<bool>? shouldRun = null, Func<Task>? completed = null) =>
            _children.Add(new FlipChild(phase, state, () => message, step, shouldRun ?? (() => true), completed ?? Nothing));

        // Before: stop the guiders that guide. What was guiding is remembered, and only that is started again.
        if (settings.StopGuidingBeforeFlip)
        {
            foreach (var guider in guiders)
            {
                Add(FlipPhase.Before, MeridianFlipState.StoppingGuiding, "Stopping guiding", new FlipStopGuidingStep(this, registry, guider));
            }
        }

        // The mount: tracking on (a retry after a failure that stopped it), the slew to the same target, and a look at what the driver says. A retry of a flip that did change sides does not slew
        // again: the mount stays where centering left it, and centering goes on from there instead of starting again from the uncorrected target.
        Add(FlipPhase.Mount, MeridianFlipState.Flipping, "Flipping the mount", new FlipTrackingStep(this, registry));
        Add(FlipPhase.Mount, MeridianFlipState.Flipping, "Flipping the mount", new FlipMarkStep(this, registry));
        Add(FlipPhase.Mount, MeridianFlipState.Flipping, "Flipping the mount", new SlewAction(registry, MountId, _plan.Target), () => !_sideChanged);
        Add(FlipPhase.Mount, MeridianFlipState.Flipping, "Checking the mount", new FlipVerifyStep(this, registry));
        if (settings.RecenterAfterFlip)
        {
            Add(FlipPhase.Mount, MeridianFlipState.Solving, "Solving", new FlipCenterStep(this));
        }

        // After: rotation, focus, guiding, dither, pause.
        if (settings.VerifyRotationAfterFlip && _plan.PointingRig.RotatorId is not null && _plan.DesiredRotationDegrees is { } rotation && _services.Rotation is { } rotationService)
        {
            var defaults = _services.PlateSolveDefaults?.Invoke() ?? new PlateSolveDefaults();
            Add(
                FlipPhase.After, MeridianFlipState.Rotating, "Verifying the rotation",
                new RotateAndVerifyAction(
                    rotationService, _plan.PointingRig, MountId, rotation, RotationService.DefaultToleranceDegrees, RotationService.DefaultMaxAttempts,
                    TimeSpan.FromSeconds(settings.SolveExposureSeconds), defaults));
        }

        if (settings.AutofocusAfterFlip && _services.FocusMetrics is { } metrics)
        {
            foreach (var rig in _plan.Setups.Where(r => r.FocuserId is not null))
            {
                Add(
                    FlipPhase.After, MeridianFlipState.Autofocusing, $"Autofocusing {rig.Name}",
                    AutofocusAction.ForRig(registry, rig, _plan.AutofocusOptions, metrics, _services.Events, _services.Loggers?.CreateLogger<AutofocusAction>(), _services.AcquisitionDefaults),
                    null, () => { _plan.OnAutofocused?.Invoke(); return Task.CompletedTask; });
            }
        }

        if (settings.RestartGuidingAfterFlip)
        {
            foreach (var guider in guiders)
            {
                Add(FlipPhase.After, MeridianFlipState.StartingGuiding, "Starting guiding", new StartGuidingAction(registry, guider), () => _wasGuiding.Contains(guider));
                Add(FlipPhase.After, MeridianFlipState.Settling, "Waiting for guiding to settle", new FlipSettleStep(this, registry, guider, dither: false), () => _wasGuiding.Contains(guider));
            }

            if (settings.DitherAfterFlip)
            {
                foreach (var guider in guiders)
                {
                    Add(FlipPhase.After, MeridianFlipState.Dithering, "Dithering", new FlipSettleStep(this, registry, guider, dither: true), () => _wasGuiding.Contains(guider));
                }
            }
        }

        if (settings.PauseAfterFlipMinutes > 0)
        {
            Add(FlipPhase.After, MeridianFlipState.PostFlipPause, "Pausing after the flip", new FlipPauseStep(TimeSpan.FromMinutes(settings.PauseAfterFlipMinutes)));
        }
    }

    // ---- state

    private double? SafeHourAngleHours()
    {
        try
        {
            return HourAngleMinutes() / 60;
        }
        catch (MeridianFlipFailedException)
        {
            return null;
        }
    }

    private async Task SetStateAsync(MeridianFlipState state, string message, double? hours, CancellationToken cancellationToken)
    {
        bool changed;
        lock (_gate)
        {
            changed = State != state || Message != message;
            State = state;
            Message = message;
        }

        if (!changed)
        {
            return;
        }

        if (_services.Events is { } events)
        {
            try
            {
                await events.PublishAsync(new MeridianFlipStateChanged(MountId, state, message, hours, Attempt, SetupNames), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "The meridian flip state could not be published");
            }
        }
    }

    internal Task ReportAsync(MeridianFlipState state, string message, CancellationToken cancellationToken) => SetStateAsync(state, message, SafeHourAngleHours(), cancellationToken);

    private void Log(string name, string template, params object?[] args) => _logger.Log(LogLevel.Information, new EventId(0, name), template, args);

    public static string FormatMinutes(double minutes)
    {
        var total = (int)Math.Round(Math.Abs(minutes) * 60);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60:00}:{total % 60:00}");
    }

    // ---- the steps

    // Stops the guider if it guides, and remembers that it did.
    private sealed class FlipStopGuidingStep(MeridianFlipGroup group, DeviceRegistry registry, DeviceId guiderId) : IResourceAwareSequenceStep
    {
        public string Name => "Stop guiding for the flip";

        public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(guiderId)];

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var guider = DeviceLookup.Resolve<IGuider>(registry, guiderId, "guider");
            if (guider.ConnectionState == DeviceConnectionState.Connected && guider.GuidingState is GuidingState.Guiding or GuidingState.Dithering or GuidingState.Starting)
            {
                lock (group._gate)
                {
                    group._wasGuiding.Add(guiderId);
                }

                await guider.StopGuidingAsync(cancellationToken);
            }

            return new SequenceStepResult();
        }
    }

    // Tracking on before the slew: a flip that was held after a failure has its mount stopped, and a retry starts it again.
    private sealed class FlipTrackingStep(MeridianFlipGroup group, DeviceRegistry registry) : IResourceAwareSequenceStep
    {
        public string Name => "Start tracking for the flip";

        public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(group.MountId)];

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            if (registry.TryGet(group.MountId, out var device) && device is IMountControl { ConnectionState: DeviceConnectionState.Connected, Telemetry: { Tracking: false } } control
                && control.Capabilities.Value is { CanSetTracking: true })
            {
                group.Log("MeridianFlipTrackingStarted", "The mount {Mount} was not tracking; tracking is started for the flip", group.MountId);
                await control.SetTrackingAsync(true, cancellationToken);
            }

            return new SequenceStepResult();
        }
    }

    // Notes the pier side before the slew.
    private sealed class FlipMarkStep(MeridianFlipGroup group, DeviceRegistry registry) : ISequenceStep
    {
        public string Name => "Meridian flip";

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            // The side before the flip is the side before the first slew: a retry of a flip that did change sides keeps it.
            if (!group._sideChanged)
            {
                group._pierBefore = registry.TryGet(group.MountId, out var device) && device is IMountControl { Telemetry: { } telemetry } ? telemetry.SideOfPier ?? PierSide.Unknown : PierSide.Unknown;
            }

            group.Log("MeridianFlipSlew", "Meridian flip slew: mount {Mount} to RA {Ra:0.####} h Dec {Dec:0.####} deg, side of pier before {Pier}", group.MountId, group._plan.Target.RightAscensionHours, group._plan.Target.DeclinationDegrees, group._pierBefore);
            return Task.FromResult(new SequenceStepResult());
        }
    }

    // What the driver says after the slew: not slewing, near the target, and the side of the pier where it reports one.
    private sealed class FlipVerifyStep(MeridianFlipGroup group, DeviceRegistry registry) : ISequenceStep
    {
        public string Name => "Check the flip";

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var mount = DeviceLookup.Resolve<IMount>(registry, group.MountId, "mount");
            if (mount.MotionState == MountMotionState.Slewing)
            {
                throw new MeridianFlipFailedException("The mount is still slewing after the flip.");
            }

            var separation = SkyMath.AngularSeparationDegrees(mount.Coordinates, group._plan.Target);
            if (separation > 2)
            {
                throw new MeridianFlipFailedException(string.Create(CultureInfo.InvariantCulture, $"The mount stands {separation:0.#}° from the target after the flip."));
            }

            var after = mount is IMountControl { Telemetry: { } telemetry } ? telemetry.SideOfPier ?? PierSide.Unknown : PierSide.Unknown;
            if (group._pierBefore != PierSide.Unknown && after != PierSide.Unknown)
            {
                group.Log(
                    "MeridianFlipMountCompleted", "Meridian flip mount completed: side of pier {Before} to {After}, {Separation:0.###} deg from the target",
                    group._pierBefore, after, separation);
                if (after != group._pierBefore)
                {
                    group._sideChanged = true;
                }
                else if (!group._sideChanged)
                {
                    // The slew went to the same target and the mount is on the same side of the pier: it did not flip (many mounts keep tracking past the meridian and do not turn for a slew to the
                    // same place). The pointing is right, so a plate solve would not find the fault, but the next exposure would be taken on the wrong side. This is a failure, not a note.
                    group.Log("MeridianFlipPierUnchanged", "The driver reports the same side of pier after the flip ({Pier}): the mount did not flip", after);
                    throw new MeridianFlipFailedException(
                        $"The mount did not change sides of the pier (it reports {after} before and after the slew). Check the settings of the mount for the pier side and the meridian limits.");
                }
            }
            else
            {
                group.Log("MeridianFlipMountCompleted", "Meridian flip mount completed: side of pier not reported, {Separation:0.###} deg from the target", separation);
            }

            return Task.FromResult(new SequenceStepResult());
        }
    }

    // Plate solves and centers with the pointing setup: the existing centering of the service, never a sync.
    private sealed class FlipCenterStep(MeridianFlipGroup group) : ISequenceStep, IServiceLeasedStep
    {
        public string Name => "Center after the flip";

        public IReadOnlyCollection<ResourceId> ServiceResources =>
            [ResourceId.ForDevice(group.MountId), ResourceId.ForMountStability(group.MountId), ResourceId.ForDevice(group._plan.PointingRig.CameraId)];

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var service = group._services.PlateSolving
                ?? throw new MeridianFlipFailedException("Centering after the flip needs plate solving, which is not available.");
            var settings = group._plan.Settings;
            var progress = new Progress<CenteringProgress>(p =>
            {
                var state = p.Stage is "Solve" ? MeridianFlipState.Solving : MeridianFlipState.Centering;
                var text = p.PointingErrorArcseconds is { } error
                    ? string.Create(CultureInfo.InvariantCulture, $"{p.Stage} #{p.Attempt} · error {error:0}\"")
                    : $"{p.Stage} #{p.Attempt}";
                _ = group.ReportAsync(state, text, CancellationToken.None);
            });
            // A flip that is tried again goes on from where the corrections of the last centering had got to, not from the uncorrected target.
            var result = await service.CenterTargetAsync(
                group._plan.Target, group._plan.PointingRig, group.MountId, settings.CenteringToleranceArcseconds, settings.MaxCenteringAttempts,
                TimeSpan.FromSeconds(settings.SolveExposureSeconds), group._services.PlateSolveDefaults?.Invoke() ?? new PlateSolveDefaults(), null, progress, cancellationToken, group._centeringStart);
            if (!result.Success)
            {
                group._centeringStart = result.Commanded;
                throw new MeridianFlipFailedException(result.Message ?? "The target could not be centered after the flip.");
            }

            group._centeringStart = null;

            group.Log(
                "MeridianFlipCenterCompleted", "Meridian flip centered: {Attempts} attempts, final error {Error} arcsec", result.Attempts,
                result.PointingErrorArcseconds?.ToString("0.#", CultureInfo.InvariantCulture) ?? "unknown");
            return new SequenceStepResult(result);
        }
    }

    // Waits for the guider to settle (and, with dither, dithers once first). The real settle of the guider, never a fixed delay.
    private sealed class FlipSettleStep(MeridianFlipGroup group, DeviceRegistry registry, DeviceId guiderId, bool dither) : IResourceAwareSequenceStep, IClaimingSequenceStep
    {
        public string Name => dither ? "Dither after the flip" : "Settle after the flip";

        public IReadOnlyCollection<ResourceId> RequiredResources =>
            dither ? [ResourceId.ForDevice(guiderId), ResourceId.ForDevice(group.MountId)] : [ResourceId.ForDevice(guiderId)];

        // A dither after the flip moves the mount: its stability is held too. Settling alone only watches the guider.
        public IReadOnlyCollection<ResourceClaim> Claims =>
            dither ? [.. ResourceClaim.AllExclusive(RequiredResources), ResourceClaim.Exclusive(ResourceId.ForMountStability(group.MountId))] : ResourceClaim.AllExclusive(RequiredResources);

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var guider = DeviceLookup.Resolve<IGuider>(registry, guiderId, "guider");
            if (dither)
            {
                if (guider is not IDitherGuider ditherGuider)
                {
                    throw new MeridianFlipFailedException($"The guider '{guiderId}' cannot dither.");
                }

                await ditherGuider.DitherAsync(group._plan.DitherAmplitudePixels, cancellationToken);
            }

            if (guider is IGuidingSettler settler)
            {
                await settler.SettleAsync(group._plan.Settle, cancellationToken);
                group.Log("MeridianFlipSettled", "Meridian flip: guiding settled ({Guider})", guiderId);
            }
            else
            {
                group.Log("MeridianFlipSettleUnavailable", "The guider {Guider} cannot report settling; the flip does not wait for it", guiderId);
            }

            if (!dither)
            {
                group.Log("MeridianFlipGuidingRestarted", "Meridian flip: guiding restarted ({Guider})", guiderId);
            }

            return new SequenceStepResult();
        }
    }

    private sealed class FlipPauseStep(TimeSpan duration) : ISequenceStep
    {
        public string Name => string.Create(CultureInfo.InvariantCulture, $"Pause {duration.TotalMinutes:0.##} min after the flip");

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(duration, cancellationToken);
            return new SequenceStepResult();
        }
    }
}

/// <summary>
/// Put before each exposure of a setup whose mount flips: asks the mount group's flip whether the exposure may start. While it may not (the exposure would not end before the flip) the setup waits
/// at safe points, so the flip can come as soon as everybody is there; when the flip is due the setup asks for it (the first one that does runs it for all), and when it is over the exposure
/// starts. An exposure that is running is never interrupted: the gate is only asked between exposures.
/// </summary>
public sealed class MeridianGateStep(MeridianFlipGroup group, double exposureSeconds, Sidera.Runtime.Sequencing.ConditionScope? stop = null) : ISequenceStep
{
    public string Name => "Meridian check";

    public MeridianFlipGroup Group => group;

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A stop condition that holds comes before the flip: there is nothing to flip for when the imaging that would follow is over. The step ends; what follows it does not start.
            if (stop?.Check() == true)
            {
                return new SequenceStepResult();
            }

            switch (await group.EvaluateAsync(exposureSeconds, cancellationToken))
            {
                case MeridianGateDecision.Clear:
                    return new SequenceStepResult();
                case MeridianGateDecision.Standstill:
                    await group.StandstillAsync(context, cancellationToken);
                    break;
                case MeridianGateDecision.Wait:
                    // At a safe point while waiting, so that the flip is not kept waiting for this setup.
                    await context.ReachSafePointAsync(cancellationToken);
                    await Task.Delay(group.PollInterval, cancellationToken);
                    break;
                default:
                    await group.FlipAsync(context, cancellationToken);
                    break;
            }
        }
    }
}
