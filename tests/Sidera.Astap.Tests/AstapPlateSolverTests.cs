using Sidera.Astap;
using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;

namespace Sidera.Astap.Tests;

public sealed class AstapPlateSolverTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "sidera-astap-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private AstapPlateSolver Solver(FakeRunner runner, FakeFileSystem? fs = null, AstapConfiguration? configuration = null) =>
        new(() => configuration ?? new AstapConfiguration(), fs ?? FakeFileSystem.Installed(), runner, tempRoot: _temp);

    private static readonly CelestialCoordinates Orion = new(5.5, -5.4);

    private static PlateSolveRequest Hinted(int width = 600, int height = 400) =>
        new(AstapSamples.Image(width, height))
        {
            ApproximateCenter = Orion,
            PixelScaleXArcsecPerPixel = 1.0,
            PixelScaleYArcsecPerPixel = 1.0,
            FieldOfViewXDegrees = 0.17,
            FieldOfViewYDegrees = 0.11,
            FocalLengthMm = 750,
        };

    private static (int, string?, bool) Solved(double ra = 82.5, double dec = -5.4) => (0, AstapSamples.SolvedIni(ra, dec, 1.0, 10), false);

    private static (int, string?, bool) NoSolution() => (1, AstapSamples.Failed, false);

    private static string After(ProcessRunRequest run, string option) => run.Arguments[run.Arguments.ToList().IndexOf(option) + 1];

    private bool TempIsEmpty() => !Directory.Exists(_temp) || Directory.GetDirectories(_temp).Length == 0;

    [Fact]
    public async Task AHintedSolve_RunsOnce_WithThePositionAndTheScale_AndGivesTheResult()
    {
        var runner = new FakeRunner((_, _) => Solved());

        var result = await Solver(runner).SolveAsync(Hinted());

        Assert.True(result.Success);
        Assert.Equal("ASTAP", result.Backend);
        Assert.Equal(5.5, result.Center!.RightAscensionHours, 9);
        Assert.Equal(1.0, result.PixelScaleArcsecPerPixel!.Value, 6);
        Assert.Equal(10, result.RotationDegrees!.Value, 9);
        Assert.Single(runner.Runs);
        var run = runner.Runs[0];
        Assert.EndsWith("astap_cli.exe", run.FileName);
        Assert.Equal("5.5", After(run, "-ra"));
        Assert.Equal("84.6", After(run, "-spd"));
        Assert.Equal("0.11", After(run, "-fov"));
        Assert.Equal("10", After(run, "-r"));
        Assert.Equal("d50", After(run, "-D"));
        var attempt = Assert.Single(result.Attempts);
        Assert.Equal("hinted", attempt.Strategy);
        Assert.True(attempt.Succeeded);
        Assert.Contains("star database D50", result.Diagnostic);
    }

    [Fact]
    public async Task TheImageIsWrittenAsFits_ForTheSolver_AndTheFolderIsGoneAfterwards()
    {
        string? header = null;
        long length = 0;
        var runner = new FakeRunner((_, run) =>
        {
            var path = After(run, "-f");
            var bytes = File.ReadAllBytes(path);
            length = bytes.Length;
            header = System.Text.Encoding.ASCII.GetString(bytes, 0, 2880);
            return Solved();
        });

        await Solver(runner).SolveAsync(Hinted());

        Assert.StartsWith("SIMPLE  =                    T", header);
        Assert.Contains("NAXIS1  =                  600", header);
        Assert.Contains("EXPTIME", header);
        Assert.Contains("FOCALLEN", header);
        Assert.Equal(0, length % 2880);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task TheFolder_IsKept_WhenTheConfigurationSaysSo()
    {
        var runner = new FakeRunner((_, _) => Solved());

        await Solver(runner, configuration: new AstapConfiguration { KeepDiagnosticFiles = true }).SolveAsync(Hinted());

        Assert.False(TempIsEmpty());
    }

    [Fact]
    public async Task WhenTheFirstTryFindsNothing_AWiderSearchWithAnotherShrinkingFollows()
    {
        var runner = new FakeRunner((n, _) => n == 1 ? NoSolution() : Solved());

        var result = await Solver(runner).SolveAsync(Hinted(3000, 2000));

        Assert.True(result.Success);
        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal("10", After(runner.Runs[0], "-r"));
        Assert.Equal("30", After(runner.Runs[1], "-r"));
        Assert.Equal("1", After(runner.Runs[0], "-z"));
        Assert.Equal("2", After(runner.Runs[1], "-z"));
        Assert.Equal(["hinted", "wider search"], result.Attempts.Select(a => a.Strategy));
        Assert.Equal([false, true], result.Attempts.Select(a => a.Succeeded));
        Assert.Equal(5.5, After(runner.Runs[1], "-ra") is var ra ? double.Parse(ra, System.Globalization.CultureInfo.InvariantCulture) : 0);
    }

    [Fact]
    public async Task WithoutBlindAllowed_ItStopsAfterTheWiderSearch()
    {
        var runner = new FakeRunner((_, _) => NoSolution());

        var result = await Solver(runner).SolveAsync(Hinted());

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailure.NoSolution, result.Failure);
        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal(2, result.Attempts.Count);
        Assert.DoesNotContain(result.Attempts, a => a.Strategy == "blind");
    }

    [Fact]
    public async Task WithBlindAllowed_ABlindSearchIsTheLastTry_WithTheWholeSky()
    {
        var runner = new FakeRunner((n, _) => n < 3 ? NoSolution() : Solved());

        var result = await Solver(runner).SolveAsync(Hinted() with { BlindAllowed = true });

        Assert.True(result.Success);
        Assert.Equal(3, runner.Runs.Count);
        var blind = runner.Runs[2];
        Assert.DoesNotContain("-ra", blind.Arguments);
        Assert.Equal("180", After(blind, "-r"));
        Assert.Equal(["hinted", "wider search", "blind"], result.Attempts.Select(a => a.Strategy));
    }

    [Fact]
    public async Task WithoutAnyHint_AndWithoutBlind_NothingIsRun()
    {
        var runner = new FakeRunner((_, _) => Solved());

        var result = await Solver(runner).SolveAsync(new PlateSolveRequest(AstapSamples.Image()));

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailure.NotSolvable, result.Failure);
        Assert.Empty(runner.Runs);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task WithoutAPosition_ButWithBlindAllowed_OneBlindSearchIsRun()
    {
        var runner = new FakeRunner((_, _) => Solved());

        var result = await Solver(runner).SolveAsync(new PlateSolveRequest(AstapSamples.Image()) { BlindAllowed = true });

        Assert.True(result.Success);
        var run = Assert.Single(runner.Runs);
        Assert.DoesNotContain("-ra", run.Arguments);
        Assert.Equal("0", After(run, "-fov")); // no field known: ASTAP finds it
        Assert.Equal("blind", Assert.Single(result.Attempts).Strategy);
    }

    [Fact]
    public async Task TheScaleAlone_GivesTheFieldOfViewHeightForTheCommandLine()
    {
        var runner = new FakeRunner((_, _) => Solved());
        var request = new PlateSolveRequest(AstapSamples.Image(600, 400)) { ApproximateCenter = Orion, PixelScaleYArcsecPerPixel = 36.0 };

        await Solver(runner).SolveAsync(request);

        Assert.Equal("4", After(runner.Runs[0], "-fov")); // 36 arcsec * 400 px = 4 degrees
    }

    [Fact]
    public async Task ASolveWithoutAHintedRadius_UsesTheConfiguredOne_AndARequestedOneWins()
    {
        var runner = new FakeRunner((_, _) => Solved());

        await Solver(runner, configuration: new AstapConfiguration { SearchRadiusDegrees = 6 }).SolveAsync(Hinted());
        await Solver(runner, configuration: new AstapConfiguration { SearchRadiusDegrees = 6 }).SolveAsync(Hinted() with { SearchRadiusDegrees = 2.5 });

        Assert.Equal("6", After(runner.Runs[0], "-r"));
        Assert.Equal("2.5", After(runner.Runs[1], "-r"));
    }

    [Fact]
    public async Task TheDownsampling_FollowsTheRequest_ThenTheConfiguration_ThenTheAutomaticRule()
    {
        var runner = new FakeRunner((_, _) => Solved());

        await Solver(runner).SolveAsync(Hinted(3000, 2000)); // 6 MP: not shrunk
        await Solver(runner).SolveAsync(Hinted(6248, 4176)); // 26 MP: half
        await Solver(runner, configuration: new AstapConfiguration { Downsample = DownsamplePolicy.Of(4) }).SolveAsync(Hinted(3000, 2000));
        await Solver(runner).SolveAsync(Hinted(3000, 2000) with { Downsample = DownsamplePolicy.Of(3) });

        Assert.Equal(["1", "2", "4", "3"], runner.Runs.Select(r => After(r, "-z")));
    }

    [Fact]
    public async Task ANotInstalledAstap_IsAFailureWithAReason_AndNothingIsRun()
    {
        var runner = new FakeRunner((_, _) => Solved());

        var result = await Solver(runner, new FakeFileSystem()).SolveAsync(Hinted());

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailure.NotAvailable, result.Failure);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task AMissingDatabase_IsAFailureWithAReason_AndNothingIsRun()
    {
        var runner = new FakeRunner((_, _) => Solved());

        var result = await Solver(runner, FakeFileSystem.Installed(databaseName: null)).SolveAsync(Hinted());

        Assert.Equal(PlateSolveFailure.NoDatabase, result.Failure);
        Assert.Contains("star database", result.Message);
        Assert.Empty(runner.Runs);
    }

    [Theory]
    [InlineData(16, PlateSolveFailure.ImageError)]
    [InlineData(32, PlateSolveFailure.NoDatabase)]
    [InlineData(77, PlateSolveFailure.Error)]
    public async Task AFailureThatAnotherTryCannotFix_IsNotRetried(int exit, PlateSolveFailure expected)
    {
        var runner = new FakeRunner((_, _) => (exit, AstapSamples.Failed, false));

        var result = await Solver(runner).SolveAsync(Hinted() with { BlindAllowed = true });

        Assert.Equal(expected, result.Failure);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task AnOutputThatCannotBeUnderstood_IsAFailureOfItsOwn()
    {
        var runner = new FakeRunner((_, _) => (0, "PLTSOLVD=T\r\nCRVAL1=1\r\n", false));

        var result = await Solver(runner).SolveAsync(Hinted());

        Assert.Equal(PlateSolveFailure.MalformedOutput, result.Failure);
    }

    [Fact]
    public async Task ATimeout_IsAFailureThatEndedTheProgram_AndIsNotRetried()
    {
        var runner = new FakeRunner((_, _) => (-1, null, true));

        var result = await Solver(runner).SolveAsync(Hinted() with { Timeout = TimeSpan.FromSeconds(5), BlindAllowed = true });

        Assert.Equal(PlateSolveFailure.Timeout, result.Failure);
        Assert.Single(runner.Runs);
        Assert.True(runner.Runs[0].Timeout <= TimeSpan.FromSeconds(5));
        Assert.Equal("timed out", Assert.Single(result.Attempts).Outcome);
        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task EachTry_GetsOnlyWhatIsLeftOfTheTimeout()
    {
        var runner = new FakeRunner((_, _) => NoSolution());

        await Solver(runner).SolveAsync(Hinted() with { Timeout = TimeSpan.FromSeconds(30), BlindAllowed = true });

        Assert.True(runner.Runs[1].Timeout <= runner.Runs[0].Timeout);
        Assert.All(runner.Runs, r => Assert.True(r.Timeout <= TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Cancelling_StopsTheSolve_AndLeavesNothingBehind()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner((n, _) =>
        {
            cts.Cancel(); // the user cancels while the first try runs; the next one never starts
            return NoSolution();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Solver(runner).SolveAsync(Hinted() with { BlindAllowed = true }, cts.Token));

        Assert.True(TempIsEmpty());
    }

    [Fact]
    public async Task ACancelledTokenBeforeTheSolve_ThrowsAtOnce_WithoutRunningAnything()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new FakeRunner((_, _) => Solved());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Solver(runner).SolveAsync(Hinted(), cts.Token));

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task TheStatus_SaysWhatIsInstalled_AndWhatIsNot()
    {
        var ready = await Solver(new FakeRunner((_, _) => Solved())).GetStatusAsync();
        var noDatabase = await Solver(new FakeRunner((_, _) => Solved()), FakeFileSystem.Installed(databaseName: null)).GetStatusAsync();
        var nothing = await Solver(new FakeRunner((_, _) => Solved()), new FakeFileSystem()).GetStatusAsync();

        Assert.True(ready.IsAvailable);
        Assert.Contains("Star database: D50", ready.Lines);
        Assert.False(noDatabase.IsAvailable);
        Assert.False(nothing.IsAvailable);
    }

    [Fact]
    public void TheSolverSaysWhatItCanDo_WithoutNamingTheProgram()
    {
        var capabilities = Solver(new FakeRunner((_, _) => Solved())).Capabilities;

        Assert.True(capabilities.HasFlag(PlateSolverCapabilities.HintedSolve));
        Assert.True(capabilities.HasFlag(PlateSolverCapabilities.BlindSolve));
        Assert.True(capabilities.HasFlag(PlateSolverCapabilities.Wcs));
        Assert.Equal("ASTAP", Solver(new FakeRunner((_, _) => Solved())).Name);
    }

    [Fact]
    public async Task AChangedConfiguration_CountsFromTheNextSolve_WithoutANewSolver()
    {
        var configuration = new AstapConfiguration { SearchRadiusDegrees = 5 };
        var runner = new FakeRunner((_, _) => Solved());
        var solver = new AstapPlateSolver(() => configuration, FakeFileSystem.Installed(), runner, tempRoot: _temp);

        await solver.SolveAsync(Hinted());
        configuration = configuration with { SearchRadiusDegrees = 8 };
        await solver.SolveAsync(Hinted());

        Assert.Equal(["5", "8"], runner.Runs.Select(r => After(r, "-r")));
    }
}
