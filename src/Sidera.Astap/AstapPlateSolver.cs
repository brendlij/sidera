using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Astrometry;
using Sidera.Core.Imaging;

namespace Sidera.Astap;

/// <summary>
/// ASTAP as a <see cref="IPlateSolver"/>. A solve writes the image as FITS into a temporary folder, runs <c>astap_cli</c> with what the request knows and reads what it wrote.
/// It is hinted first: with a position it searches around it, with a wider search and another shrinking as the second try, and without any hint only when
/// the request allows a blind search. The tries are fixed and are all in the result and in the log; the folder is deleted at the end whatever happened, unless the
/// configuration keeps it. Nothing of ASTAP (options, files, exit codes) is visible outside this project.
/// </summary>
public sealed class AstapPlateSolver : IPlateSolver
{
    public const string BackendName = "ASTAP";

    private readonly Func<AstapConfiguration> _configuration;
    private readonly IAstapFileSystem _fileSystem;
    private readonly IProcessRunner _runner;
    private readonly ILogger _logger;
    private readonly string _tempRoot;

    public AstapPlateSolver(
        Func<AstapConfiguration> configuration, IAstapFileSystem? fileSystem = null, IProcessRunner? runner = null, ILogger? logger = null, string? tempRoot = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _fileSystem = fileSystem ?? new SystemAstapFileSystem();
        _runner = runner ?? new SystemProcessRunner();
        _logger = logger ?? NullLogger.Instance;
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "Sidera", "solve");
    }

    public string Name => BackendName;

    public PlateSolverCapabilities Capabilities =>
        PlateSolverCapabilities.HintedSolve | PlateSolverCapabilities.BlindSolve | PlateSolverCapabilities.Rotation
        | PlateSolverCapabilities.PixelScale | PlateSolverCapabilities.Wcs | PlateSolverCapabilities.Parity;

    public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AstapLocator.Locate(_configuration(), _fileSystem).ToStatus());

    public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var configuration = _configuration();
        var image = request.Image;
        _logger.LogInformation(
            PlateSolveLog.Started, "PlateSolveStarted: backend {Backend}, image {Width}x{Height}, approximate center {Center}, search radius {Radius}, blind allowed {Blind}",
            BackendName, image.Width, image.Height, Describe(request), request.SearchRadiusDegrees ?? configuration.SearchRadiusDegrees, request.BlindAllowed);

        if (configuration.Problem() is { } invalid)
        {
            return Fail(PlateSolveFailure.Error, invalid, clock, []);
        }

        var installation = AstapLocator.Locate(configuration, _fileSystem);
        if (!installation.HasExecutable)
        {
            return Fail(PlateSolveFailure.NotAvailable, installation.ExecutableProblem ?? "ASTAP was not found.", clock, []);
        }

        if (!installation.HasDatabase)
        {
            return Fail(PlateSolveFailure.NoDatabase, "ASTAP has no star database. Install one (for example D50); Sidera does not.", clock, []);
        }

        var plan = Plan(request, configuration);
        if (plan.Count == 0)
        {
            return Fail(
                PlateSolveFailure.NotSolvable,
                "The image has no position hint and a blind search is not allowed. Give a position (the mount's) or allow a blind solve.", clock, []);
        }

        var timeout = request.Timeout ?? configuration.Timeout;
        var folder = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var attempts = new List<PlateSolveAttempt>();
        try
        {
            Directory.CreateDirectory(folder);
            var imagePath = Path.Combine(folder, "solve.fits");
            await WriteImageAsync(imagePath, request).ConfigureAwait(false);

            AstapOutcome? last = null;
            for (var i = 0; i < plan.Count; i++)
            {
                var step = plan[i];
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Fail(PlateSolveFailure.Timeout, $"The solve ran out of its {timeout.TotalSeconds:0} s.", clock, attempts);
                }

                var attemptClock = Stopwatch.StartNew();
                var outputBase = Path.Combine(folder, $"attempt{i + 1}");
                var invocation = new AstapInvocation(
                    imagePath, outputBase, step.RadiusDegrees, step.Center?.RightAscensionHours, step.Center?.DeclinationDegrees,
                    step.FieldOfViewHeightDegrees, step.Downsample, installation.DatabaseDirectory,
                    configuration.DatabaseAbbreviation ?? installation.Database!.Name);
                _logger.LogInformation(
                    PlateSolveLog.AstapStarted, "AstapStarted: attempt {Attempt} of {Count} ({Strategy}), radius {Radius}, downsample {Downsample}, database {Database}",
                    i + 1, plan.Count, step.Strategy, step.RadiusDegrees, step.Downsample, installation.Database!.Name);

                ProcessRunResult run;
                try
                {
                    run = await _runner.RunAsync(
                        new ProcessRunRequest(installation.ExecutablePath!, AstapCommandBuilder.Build(invocation), remaining) { WorkingDirectory = folder },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException ex)
                {
                    return Fail(PlateSolveFailure.NotAvailable, ex.Message, clock, attempts);
                }

                if (run.TimedOut)
                {
                    attempts.Add(new PlateSolveAttempt(step.Strategy, false, "timed out", attemptClock.Elapsed, step.RadiusDegrees, step.Downsample));
                    _logger.LogWarning(PlateSolveLog.AstapExited, "AstapExited: attempt {Attempt} timed out and was ended", i + 1);
                    return Fail(PlateSolveFailure.Timeout, $"The solve ran out of its {timeout.TotalSeconds:0} s; ASTAP was ended.", clock, attempts);
                }

                var ini = ReadIni(outputBase + ".ini");
                last = AstapResultParser.Parse(run.ExitCode, ini, image.Width, image.Height);
                attempts.Add(new PlateSolveAttempt(
                    step.Strategy, last.Solved, last.Solved ? "solved" : last.Message ?? "failed", attemptClock.Elapsed, step.RadiusDegrees, step.Downsample));
                _logger.LogInformation(
                    PlateSolveLog.AstapExited, "AstapExited: attempt {Attempt} exit code {ExitCode} after {DurationMs:0} ms, solved {Solved}{Truncated}",
                    i + 1, run.ExitCode, attemptClock.Elapsed.TotalMilliseconds, last.Solved, run.OutputTruncated ? " (output truncated)" : string.Empty);

                if (last.Solved)
                {
                    return Succeeded(last, clock, attempts, installation, step.Strategy);
                }

                // Another try helps only when ASTAP looked and found nothing; a missing database or an unreadable image is no better the second time.
                if (last.Failure is not (PlateSolveFailure.NoSolution or PlateSolveFailure.NotEnoughStars))
                {
                    break;
                }
            }

            return Fail(last?.Failure ?? PlateSolveFailure.Error, last?.Message ?? "ASTAP did not solve the image.", clock, attempts);
        }
        finally
        {
            if (!configuration.KeepDiagnosticFiles)
            {
                TryDelete(folder);
            }
        }
    }

    private sealed record Step(string Strategy, Sidera.Core.Mounts.CelestialCoordinates? Center, double RadiusDegrees, double? FieldOfViewHeightDegrees, int Downsample);

    // The tries, in order: hinted; the same place wider with another shrinking; blind. Fixed by the request and the configuration, so a solve is repeatable.
    private static List<Step> Plan(PlateSolveRequest request, AstapConfiguration configuration)
    {
        var image = request.Image;
        var scale = request.PixelScaleYArcsecPerPixel ?? request.PixelScaleXArcsecPerPixel;
        var fovHeight = request.FieldOfViewYDegrees
            ?? (request.PixelScaleYArcsecPerPixel is { } sy ? sy * image.Height / Sidera.Core.Astrometry.SkyMath.ArcsecondsPerDegree : null);
        var policy = request.Downsample.IsAuto ? configuration.Downsample : request.Downsample;
        var factor = DownsamplePlanner.Resolve(policy, image.Width, image.Height, scale);
        var steps = new List<Step>();

        if (request.ApproximateCenter is { } center)
        {
            var radius = request.SearchRadiusDegrees ?? configuration.SearchRadiusDegrees;
            steps.Add(new Step("hinted", center, radius, fovHeight, factor));
            var wider = Math.Min(Math.Max(radius * 3, radius + 10), 90);
            var other = factor > 1 ? factor / 2 : 2;
            if (wider > radius)
            {
                steps.Add(new Step("wider search", center, wider, fovHeight, other));
            }
        }

        if (request.BlindAllowed)
        {
            steps.Add(new Step("blind", null, 180, fovHeight, factor));
        }

        return steps;
    }

    private static async Task WriteImageAsync(string path, PlateSolveRequest request)
    {
        var image = request.Image;
        var metadata = new FitsMetadata
        {
            ExposureSeconds = image.Frame.ExposureDuration.TotalSeconds,
            PixelSizeXMicrons = image.PixelSizeXMicrons,
            PixelSizeYMicrons = image.PixelSizeYMicrons,
            FocalLengthMm = request.FocalLengthMm,
            ObservedAt = image.ObservedAt,
            RightAscensionDegrees = request.ApproximateCenter is { } c ? Sidera.Core.Astrometry.SkyMath.HoursToDegrees(c.RightAscensionHours) : null,
            DeclinationDegrees = request.ApproximateCenter?.DeclinationDegrees,
        };
        var bytes = FitsImageWriter.Write(image.Frame, metadata);
        await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
    }

    private static string? ReadIni(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private PlateSolveResult Succeeded(AstapOutcome outcome, Stopwatch clock, List<PlateSolveAttempt> attempts, AstapInstallation installation, string strategy)
    {
        var result = new PlateSolveResult
        {
            Success = true,
            Center = outcome.Center,
            RotationDegrees = outcome.RotationDegrees,
            PixelScaleXArcsecPerPixel = outcome.PixelScaleXArcsecPerPixel,
            PixelScaleYArcsecPerPixel = outcome.PixelScaleYArcsecPerPixel,
            FieldOfViewXDegrees = outcome.FieldOfViewXDegrees,
            FieldOfViewYDegrees = outcome.FieldOfViewYDegrees,
            Parity = outcome.Parity,
            Wcs = outcome.Wcs,
            Duration = clock.Elapsed,
            Backend = BackendName,
            Attempts = attempts,
            Diagnostic = string.Create(
                CultureInfo.InvariantCulture, $"Solved by the {strategy} search ({attempts.Count} {(attempts.Count == 1 ? "try" : "tries")}), star database {installation.Database!.Name}."),
        };
        _logger.LogInformation(
            PlateSolveLog.Completed, "PlateSolveCompleted: backend {Backend} in {DurationMs:0} ms, center RA {Ra:0.#####} h Dec {Dec:0.#####}, scale {Scale:0.###} arcsec/px, rotation {Rotation:0.##}",
            BackendName, clock.Elapsed.TotalMilliseconds, result.Center!.RightAscensionHours, result.Center.DeclinationDegrees, result.PixelScaleArcsecPerPixel, result.RotationDegrees);
        return result;
    }

    private PlateSolveResult Fail(PlateSolveFailure failure, string message, Stopwatch clock, IReadOnlyList<PlateSolveAttempt> attempts)
    {
        _logger.LogWarning(PlateSolveLog.Failed, "PlateSolveFailed: backend {Backend}, {Failure}: {Message} ({Attempts} tries, {DurationMs:0} ms)", BackendName, failure, message, attempts.Count, clock.Elapsed.TotalMilliseconds);
        return PlateSolveResult.Failed(BackendName, failure, message, clock.Elapsed, [.. attempts]);
    }

    private static string Describe(PlateSolveRequest request) => request.ApproximateCenter is { } c
        ? string.Create(CultureInfo.InvariantCulture, $"RA {c.RightAscensionHours:0.####} h Dec {c.DeclinationDegrees:0.####}")
        : "none";

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary folder is harmless; it is in the temporary folder of the system.
        }
    }
}

/// <summary>The events of the log for a solve.</summary>
internal static class PlateSolveLog
{
    public static readonly EventId Started = new(5001, "PlateSolveStarted");
    public static readonly EventId Completed = new(5002, "PlateSolveCompleted");
    public static readonly EventId Failed = new(5003, "PlateSolveFailed");
    public static readonly EventId AstapStarted = new(5010, "AstapStarted");
    public static readonly EventId AstapExited = new(5011, "AstapExited");
}
