using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using Sidera.Astap;
using Sidera.Core;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Xunit.Abstractions;

namespace Sidera.Astap.Tests;

/// <summary>A test against the real ASTAP of this computer; it runs only when <c>SIDERA_ASTAP_TESTS=1</c> is set. It moves nothing and needs no hardware.</summary>
public sealed class AstapFactAttribute : FactAttribute
{
    public AstapFactAttribute(string gate = "SIDERA_ASTAP_TESTS")
    {
        if (SideraEnvironment.Get(gate) != "1")
        {
            Skip = $"Needs the real ASTAP. Set {gate}=1 to run it.";
        }
    }
}

/// <summary>
/// The real ASTAP: it is found, its star database is detected, a file that Sidera writes is read by it, and, when a database and an image are there, a known image is solved.
/// <c>SIDERA_ASTAP_PATH</c> and <c>SIDERA_ASTAP_DATABASE</c> point at a program and a folder that are not in the usual places. A known image is a file (FITS, PNG,
/// JPEG) in <c>SIDERA_ASTAP_TEST_IMAGE</c> with its approximate position in <c>SIDERA_ASTAP_TEST_RA</c> (hours) and <c>SIDERA_ASTAP_TEST_DEC</c> (degrees).
/// Whatever the environment lacks (no database, no image) is said as NOT RUN with the reason; it is not a failure of Sidera.
/// </summary>
public sealed class RealAstapTests(ITestOutputHelper output)
{
    private static AstapConfiguration Configuration => new()
    {
        ExecutablePath = SideraEnvironment.Get("SIDERA_ASTAP_PATH"),
        DatabasePath = SideraEnvironment.Get("SIDERA_ASTAP_DATABASE"),
    };

    [AstapFact]
    public void TheRealAstap_IsFound_AndItsDatabaseIsDetectedOrReportedMissing()
    {
        var installation = AstapLocator.Locate(Configuration, new SystemAstapFileSystem());

        foreach (var line in installation.ToStatus().Lines)
        {
            output.WriteLine(line);
        }

        Assert.True(installation.HasExecutable, installation.ExecutableProblem);
        output.WriteLine(installation.HasDatabase
            ? $"database {installation.Database!.Name}: {installation.Database.FileCount} files in {installation.Database.Directory}"
            : "NO DATABASE: install one (for example D50) to solve");
    }

    [AstapFact]
    public async Task TheRealAstap_ReadsTheFileThatSideraWrites_AndSaysSomethingAboutTheSky()
    {
        var installation = AstapLocator.Locate(Configuration, new SystemAstapFileSystem());
        Assert.True(installation.HasExecutable, installation.ExecutableProblem);

        // An image without stars: ASTAP must read it (no "cannot read the image") and then fail for a reason of the sky or of the database.
        var solver = new AstapPlateSolver(() => Configuration);
        var request = new PlateSolveRequest(new PlateSolveImage(new CameraFrame(800, 600, NoisyBackground(800, 600), TimeSpan.FromSeconds(1))))
        {
            ApproximateCenter = new CelestialCoordinates(5.5, -5.4),
            PixelScaleXArcsecPerPixel = 3.0,
            PixelScaleYArcsecPerPixel = 3.0,
            FieldOfViewYDegrees = 0.5,
            Timeout = TimeSpan.FromSeconds(90),
        };

        var result = await solver.SolveAsync(request);

        output.WriteLine($"{result.Failure}: {result.Message} ({result.Attempts.Count} tries, {result.Duration.TotalSeconds:0.0} s)");
        foreach (var attempt in result.Attempts)
        {
            output.WriteLine($"  {attempt.Strategy}: {attempt.Outcome}");
        }

        Assert.False(result.Success); // there is nothing to solve in noise
        Assert.NotEqual(PlateSolveFailure.ImageError, result.Failure); // the file of Sidera was read
        Assert.NotEqual(PlateSolveFailure.MalformedOutput, result.Failure);
    }

    [AstapFact]
    public async Task TheRealAstap_SolvesAKnownImage_WhenThereIsADatabaseAndAnImage()
    {
        var installation = AstapLocator.Locate(Configuration, new SystemAstapFileSystem());
        var image = SideraEnvironment.Get("SIDERA_ASTAP_TEST_IMAGE");
        if (!installation.IsUsable || string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            output.WriteLine($"NOT RUN: needs a star database ({(installation.HasDatabase ? "present" : "MISSING")}) and SIDERA_ASTAP_TEST_IMAGE ({(File.Exists(image ?? string.Empty) ? "present" : "not set")}).");
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "sidera-astap-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var copy = Path.Combine(folder, Path.GetFileName(image));
            File.Copy(image, copy);
            var ra = double.TryParse(SideraEnvironment.Get("SIDERA_ASTAP_TEST_RA"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : (double?)null;
            var dec = double.TryParse(SideraEnvironment.Get("SIDERA_ASTAP_TEST_DEC"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
            var invocation = new AstapInvocation(
                copy, Path.Combine(folder, "out"), ra is null ? 180 : 15, ra, dec, null, 0, installation.DatabaseDirectory, installation.Database!.Name);

            var run = await new SystemProcessRunner().RunAsync(
                new ProcessRunRequest(installation.ExecutablePath!, AstapCommandBuilder.Build(invocation), TimeSpan.FromMinutes(3)), CancellationToken.None);
            var ini = File.Exists(Path.Combine(folder, "out.ini")) ? File.ReadAllText(Path.Combine(folder, "out.ini")) : null;
            var outcome = AstapResultParser.Parse(run.ExitCode, ini, 1, 1);

            output.WriteLine($"exit {run.ExitCode} in {run.Duration.TotalSeconds:0.0} s; solved {outcome.Solved}; {outcome.Message}");
            if (outcome.Solved)
            {
                output.WriteLine($"RA {outcome.Center!.RightAscensionHours:0.0000} h Dec {outcome.Center.DeclinationDegrees:0.0000} scale {outcome.PixelScaleXArcsecPerPixel:0.000} rotation {outcome.RotationDegrees:0.00} parity {outcome.Parity}");
            }

            Assert.True(outcome.Solved, outcome.Message);
            if (ra is { } expectedRa && dec is { } expectedDec)
            {
                var separation = SkyMath.AngularSeparationDegrees(outcome.Center!, new CelestialCoordinates(expectedRa, expectedDec));
                output.WriteLine($"{separation:0.000} degrees from the given position");
                Assert.True(separation < 5, "the solution is far from the hinted position");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ushort[] NoisyBackground(int width, int height)
    {
        var random = new Random(7);
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(1000 + random.Next(0, 40));
        }

        return pixels;
    }

    [AstapFact]
    public async Task KnownFitsPixelsSolveThroughSideraWriterAndBackend()
    {
        var path = SideraEnvironment.Get("SIDERA_ASTAP_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { output.WriteLine("NOT TESTED: no known FITS image configured."); return; }
        using var stream = File.OpenRead(path);
        var values = new Dictionary<string, string>();
        var block = new byte[2880]; bool end = false;
        while (!end)
        {
            stream.ReadExactly(block);
            for (var i = 0; i < block.Length; i += 80)
            {
                var card = Encoding.ASCII.GetString(block, i, 80);
                var key = card[..8].Trim();
                if (key == "END") { end = true; break; }
                if (card[8] == '=') values[key] = card[10..].Split('/')[0].Trim();
            }
        }
        double Number(string key) => double.Parse(values[key], CultureInfo.InvariantCulture);
        Assert.Equal(16, Number("BITPIX")); Assert.Equal(1, Number("BSCALE")); Assert.Equal(32768, Number("BZERO"));
        var width = (int)Number("NAXIS1"); var height = (int)Number("NAXIS2");
        var pixels = new ushort[width * height]; var row = new byte[width * 2];
        for (var y = height - 1; y >= 0; y--)
        {
            stream.ReadExactly(row);
            for (var x = 0; x < width; x++) pixels[y * width + x] = (ushort)(BinaryPrimitives.ReadInt16BigEndian(row.AsSpan(x * 2)) + 32768);
        }
        var frame = new CameraFrame(width, height, pixels, TimeSpan.FromSeconds(Number("EXPTIME")));
        var rig = new Sidera.Core.Rigs.Rig(new("validation"), "Known FITS", new("file"),
            new Sidera.Core.Rigs.OpticalTrain(Number("FOCALLEN"), pixelSizeXMicrons: Number("XPIXSZ"), pixelSizeYMicrons: Number("YPIXSZ")));
        var request = PlateSolveHintResolver.Resolve(frame, rig, null, SkyMath.FromDegrees(Number("RA"), Number("DEC")), new PlateSolveDefaults());
        var solver = new AstapPlateSolver(() => Configuration);
        var result = await solver.SolveAsync(request);
        output.WriteLine($"{result.Success}: {result.Message}; RA {result.Center?.RightAscensionHours}, Dec {result.Center?.DeclinationDegrees}, scale {result.PixelScaleArcsecPerPixel}, parity {result.Parity}, duration {result.Duration}");
        Assert.True(result.Success, result.Message);
        var expectedRa = double.TryParse(SideraEnvironment.Get("SIDERA_ASTAP_TEST_RA"), NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedHours) ? expectedHours : Number("RA") / 15;
        var expectedDec = double.TryParse(SideraEnvironment.Get("SIDERA_ASTAP_TEST_DEC"), NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedDegrees) ? expectedDegrees : Number("DEC");
        Assert.InRange(SkyMath.AngularSeparationDegrees(new(expectedRa, expectedDec), result.Center!), 0, 5);
        var diagnostics = PlateSolveDiagnostics.From(request, result);
        output.WriteLine($"Expected scale {diagnostics.ExpectedScale}; solved {diagnostics.SolvedScale}; difference {diagnostics.ScaleDifferencePercent}%; focal estimate {diagnostics.EstimatedFocalLengthMm} mm");
        Assert.InRange(Math.Abs(diagnostics.ScaleDifferencePercent!.Value), 0, 2);
    }
}
