using System.Globalization;
using Sidera.Astap;
using Sidera.Core.Astrometry;

namespace Sidera.Astap.Tests;

public sealed class AstapBuildingBlocksTests
{
    // ---- Command line

    [Fact]
    public void AHintedInvocation_GivesThePositionAsHoursAndTheSouthPoleDistance()
    {
        var args = AstapCommandBuilder.Build(new AstapInvocation(@"C:\tmp\a b\solve.fits", @"C:\tmp\a b\out", 10, 5.5, -5.4, 1.2, 2, @"C:\Program Files\astap", "d50"));

        Assert.Equal(
            ["-f", @"C:\tmp\a b\solve.fits", "-o", @"C:\tmp\a b\out", "-r", "10", "-ra", "5.5", "-spd", "84.6", "-fov", "1.2", "-z", "2", "-wcs", "-d", @"C:\Program Files\astap", "-D", "d50"],
            args);
    }

    [Fact]
    public void ABlindInvocation_HasNoPosition_AndAnAutomaticFieldWhenNoneIsKnown()
    {
        var args = AstapCommandBuilder.Build(new AstapInvocation("a.fits", "out", 180, null, null, null, 1, null, null));

        Assert.DoesNotContain("-ra", args);
        Assert.DoesNotContain("-spd", args);
        Assert.DoesNotContain("-d", args);
        Assert.Equal("0", args[args.ToList().IndexOf("-fov") + 1]);
        Assert.Equal("180", args[args.ToList().IndexOf("-r") + 1]);
    }

    [Fact]
    public void NumbersInTheCommandLine_UseADot_WhateverTheCultureIs()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var args = AstapCommandBuilder.Build(new AstapInvocation("a.fits", "out", 7.5, 12.25, 45.5, 1.75, 2, null, null));

            Assert.Contains("12.25", args);
            Assert.Contains("135.5", args);
            Assert.Contains("1.75", args);
            Assert.DoesNotContain(args, a => a.Contains(','));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void ThePathsAreSingleArguments_SoNoQuotingIsNeeded()
    {
        var args = AstapCommandBuilder.Build(new AstapInvocation(@"C:\a b\x.fits", @"C:\a b\out", 10, null, null, null, 1, @"D:\Star Data", null));

        Assert.Contains(@"C:\a b\x.fits", args);
        Assert.Contains(@"D:\Star Data", args);
    }

    // ---- Output

    [Fact]
    public void ASolution_IsReadWithPositionScaleRotationParityAndFieldOfView()
    {
        var ini = AstapSamples.SolvedIni(83.7, -5.39, 1.034, 12.3);

        var o = AstapResultParser.Parse(0, ini, 6248, 4176);

        Assert.True(o.Solved);
        Assert.Equal(83.7 / 15.0, o.Center!.RightAscensionHours, 9);
        Assert.Equal(-5.39, o.Center.DeclinationDegrees, 9);
        Assert.Equal(1.034, o.PixelScaleXArcsecPerPixel!.Value, 6);
        Assert.Equal(1.034, o.PixelScaleYArcsecPerPixel!.Value, 6);
        Assert.Equal(12.3, o.RotationDegrees!.Value, 9);
        Assert.Equal(PlateSolveParity.Normal, o.Parity);
        Assert.Equal(1.034 * 6248 / 3600, o.FieldOfViewXDegrees!.Value, 6);
        Assert.Equal(1.034 * 4176 / 3600, o.FieldOfViewYDegrees!.Value, 6);
        Assert.Equal(3124.5, o.Wcs!.ReferencePixelX, 6);
        Assert.Equal(83.7, o.Wcs.ReferenceRightAscensionDegrees, 9);
    }

    [Fact]
    public void ThePositionIsRightAscensionInHours_NotDegrees()
    {
        var o = AstapResultParser.Parse(0, AstapSamples.SolvedIni(180, 0.5, 1, 0), 100, 100);

        Assert.Equal(12.0, o.Center!.RightAscensionHours, 9);
    }

    [Theory]
    [InlineData(359.999999999)]
    [InlineData(360.0)]
    [InlineData(-1.0)]
    public void ARightAscensionAtTheWrap_StaysInsideZeroToTwentyFourHours(double raDegrees)
    {
        var o = AstapResultParser.Parse(0, AstapSamples.SolvedIni(raDegrees, 10, 1, 0), 100, 100);

        Assert.True(o.Center!.RightAscensionHours >= 0 && o.Center.RightAscensionHours < 24);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(-120)]
    [InlineData(179.5)]
    public void TheRotation_IsTheOneOfTheFile_AndTheOneOfTheMatrixWhenTheFileHasNone(double rotation)
    {
        var withRota = AstapResultParser.Parse(0, AstapSamples.SolvedIni(10, 10, 1, rotation), 100, 100);
        var ini = string.Join("\n", AstapSamples.SolvedIni(10, 10, 1, rotation).Split('\n').Where(l => !l.StartsWith("CROTA")));
        var fromMatrix = AstapResultParser.Parse(0, ini, 100, 100);

        Assert.Equal(rotation, withRota.RotationDegrees!.Value, 9);
        Assert.Equal(rotation, fromMatrix.RotationDegrees!.Value, 6);
    }

    [Fact]
    public void AMirroredImage_IsReportedAsMirrored()
    {
        var o = AstapResultParser.Parse(0, AstapSamples.SolvedIni(10, 10, 1, 20, mirrored: true), 100, 100);

        Assert.Equal(PlateSolveParity.Mirrored, o.Parity);
    }

    [Fact]
    public void WithoutAMatrix_TheScaleComesFromCdelt_AndTheMatrixIsBuiltFromCdeltAndTheRotation()
    {
        var o = AstapResultParser.Parse(0, AstapSamples.SolvedIni(10, 10, 2.5, 30, withCd: false), 100, 100);

        Assert.Equal(2.5, o.PixelScaleXArcsecPerPixel!.Value, 6);
        Assert.Equal(Math.Cos(30 * Math.PI / 180) * -2.5 / 3600, o.Wcs!.Cd11, 12);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(180, 180)]
    [InlineData(-180, 180)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void TheRotationIsNormalizedToMinus180Exclusive_To180Inclusive(double input, double expected) =>
        Assert.Equal(expected, AstapResultParser.NormalizeRotation(input), 9);

    [Theory]
    [InlineData(1, PlateSolveFailure.NoSolution)]
    [InlineData(2, PlateSolveFailure.NotEnoughStars)]
    [InlineData(16, PlateSolveFailure.ImageError)]
    [InlineData(32, PlateSolveFailure.NoDatabase)]
    [InlineData(33, PlateSolveFailure.NoDatabase)]
    [InlineData(34, PlateSolveFailure.Error)]
    [InlineData(99, PlateSolveFailure.Error)]
    public void TheExitCodes_BecomeFailuresOfTheirOwn(int exit, PlateSolveFailure expected)
    {
        var o = AstapResultParser.Parse(exit, AstapSamples.Failed, 100, 100);

        Assert.False(o.Solved);
        Assert.Equal(expected, o.Failure);
        Assert.NotNull(o.Message);
    }

    [Fact]
    public void AFileThatSaysItDidNotSolve_IsNoSolution_EvenWithExitZero()
    {
        var o = AstapResultParser.Parse(0, "PLTSOLVD=F\r\n", 100, 100);

        Assert.Equal(PlateSolveFailure.NoSolution, o.Failure);
    }

    [Fact]
    public void AMissingFile_AfterAFailureExitCode_StillGivesTheFailure()
    {
        Assert.Equal(PlateSolveFailure.NoSolution, AstapResultParser.Parse(1, null, 100, 100).Failure);
    }

    [Fact]
    public void AFailureThatAstapExplained_CarriesItsWords_ShortenedAndNeverTheWholeOutput()
    {
        var o = AstapResultParser.Parse(1, "PLTSOLVD=F\r\nERROR=" + new string('x', 1000) + "\r\n", 100, 100);

        Assert.True(o.Message!.Length < 300);
        Assert.Contains("...", o.Message);
    }

    [Theory]
    [InlineData("PLTSOLVD=T\r\nCRVAL1=10\r\n")] // no CRVAL2
    [InlineData("PLTSOLVD=T\r\nCRVAL1=10\r\nCRVAL2=20\r\n")] // no scale
    [InlineData("PLTSOLVD=T\r\nCRVAL1=abc\r\nCRVAL2=20\r\nCDELT1=-0.0003\r\nCDELT2=0.0003\r\n")]
    [InlineData("PLTSOLVD=T\r\nCRVAL1=10\r\nCRVAL2=95\r\nCDELT1=-0.0003\r\nCDELT2=0.0003\r\n")]
    public void ASolutionWithoutWhatItNeeds_IsMalformed_NotAResultWithZeros(string ini)
    {
        var o = AstapResultParser.Parse(0, ini, 100, 100);

        Assert.False(o.Solved);
        Assert.Equal(PlateSolveFailure.MalformedOutput, o.Failure);
    }

    [Fact]
    public void ANumberWithADecimalComma_IsNotAValue()
    {
        // ASTAP writes a dot; a comma would be read as a different number and is refused instead of being guessed at.
        var o = AstapResultParser.Parse(0, "PLTSOLVD=T\r\nCRVAL1=10,5\r\nCRVAL2=20\r\nCDELT1=-0.0003\r\nCDELT2=0.0003\r\n", 100, 100);

        Assert.Equal(PlateSolveFailure.MalformedOutput, o.Failure);
    }

    [Fact]
    public void TheParsingDoesNotDependOnTheCultureOfTheComputer()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var o = AstapResultParser.Parse(0, AstapSamples.SolvedIni(83.7, -5.39, 1.034, 12.3), 6248, 4176);

            Assert.Equal(-5.39, o.Center!.DeclinationDegrees, 9);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void AFitsStyleFile_WithQuotesAndComments_IsReadToo()
    {
        const string wcs = "PLTSOLVD= T / solved\r\nCRVAL1  =  8.37E+001 / ra\r\nCRVAL2  = -5.39E+000\r\nCDELT1  = -2.8E-004\r\nCDELT2  =  2.8E-004\r\nCROTA2  = 12.3\r\n";

        var o = AstapResultParser.Parse(0, wcs, 100, 100);

        Assert.True(o.Solved);
        Assert.Equal(83.7, o.Center!.RightAscensionHours * 15, 9);
    }

    // ---- Installation and database

    [Fact]
    public void AnInstallationInProgramFiles_IsFound_AndTheConsoleProgramIsPreferred()
    {
        var found = AstapLocator.Locate(new AstapConfiguration(), FakeFileSystem.Installed());

        Assert.Equal(@"C:\Program Files\astap\astap_cli.exe", found.ExecutablePath);
        Assert.False(found.ExecutableWasConfigured);
        Assert.True(found.IsUsable);
    }

    [Fact]
    public void WithoutTheConsoleProgram_TheWindowProgramIsUsed()
    {
        var found = AstapLocator.Locate(new AstapConfiguration(), FakeFileSystem.Installed(console: false));

        Assert.EndsWith("astap.exe", found.ExecutablePath);
    }

    [Fact]
    public void ANotInstalledAstap_IsClearlyNotFound()
    {
        var found = AstapLocator.Locate(new AstapConfiguration(), new FakeFileSystem());

        Assert.False(found.HasExecutable);
        var status = found.ToStatus();
        Assert.False(status.IsAvailable);
        Assert.Contains("not found", status.Problem);
    }

    [Fact]
    public void AConfiguredPath_Counts_AndIsReportedMissingWhenItIsMissing_NotReplacedByASearch()
    {
        var fs = FakeFileSystem.Installed();

        var missing = AstapLocator.Locate(new AstapConfiguration { ExecutablePath = @"D:\tools\astap.exe" }, fs);

        Assert.False(missing.HasExecutable);
        Assert.Contains(@"D:\tools\astap.exe", missing.ExecutableProblem);

        fs.Files.Add(@"D:\tools\astap.exe");
        var found = AstapLocator.Locate(new AstapConfiguration { ExecutablePath = @"D:\tools\astap.exe" }, fs);
        Assert.Equal(@"D:\tools\astap.exe", found.ExecutablePath);
        Assert.True(found.ExecutableWasConfigured);
    }

    [Fact]
    public void AConfiguredWindowProgram_IsSwitchedToTheConsoleOneBesideIt()
    {
        var found = AstapLocator.Locate(new AstapConfiguration { ExecutablePath = @"C:\Program Files\astap\astap.exe" }, FakeFileSystem.Installed());

        Assert.EndsWith("astap_cli.exe", found.ExecutablePath);
    }

    [Fact]
    public void TheProgramAlone_IsNotADatabase()
    {
        var found = AstapLocator.Locate(new AstapConfiguration(), FakeFileSystem.Installed(databaseName: null));

        Assert.True(found.HasExecutable);
        Assert.False(found.HasDatabase);
        Assert.False(found.IsUsable);
        var status = found.ToStatus();
        Assert.False(status.IsAvailable);
        Assert.Contains(status.Lines, l => l.StartsWith("ASTAP installed"));
        Assert.Contains(status.Lines, l => l.StartsWith("No usable star database found"));
    }

    [Theory]
    [InlineData("d50", "D50")]
    [InlineData("d20", "D20")]
    [InlineData("d80", "D80")]
    [InlineData("g18", "G18")]
    public void AnInstalledDatabase_IsNamedByItsTiles_NotAssumedToBeD50(string tiles, string expected)
    {
        var found = AstapLocator.Locate(new AstapConfiguration(), FakeFileSystem.Installed(tiles));

        Assert.Equal(expected, found.Database!.Name);
        Assert.Contains($"Star database: {expected}", found.ToStatus().Lines);
    }

    [Fact]
    public void WithSeveralDatabases_TheChosenOneCounts_AndOtherwiseOneIsPickedTheSameWayEveryTime()
    {
        var fs = FakeFileSystem.Installed("d50");
        fs.Folders[@"C:\Program Files\astap"].AddRange(["d20_0001.1476", "d20_0002.1476"]);

        Assert.Equal("D20", AstapLocator.Locate(new AstapConfiguration { DatabaseAbbreviation = "d20" }, fs).Database!.Name);
        Assert.Equal("D50", AstapLocator.Locate(new AstapConfiguration(), fs).Database!.Name);
        Assert.Null(AstapLocator.Locate(new AstapConfiguration { DatabaseAbbreviation = "d80" }, fs).Database);
    }

    [Fact]
    public void ADatabaseFolderOfItsOwn_IsUsed_WhenConfigured()
    {
        var fs = FakeFileSystem.Installed(databaseName: null);
        fs.Folders[@"D:\Stars"] = ["d50_0101.1476", "d50_0102.1476", "readme.txt"];

        var found = AstapLocator.Locate(new AstapConfiguration { DatabasePath = @"D:\Stars" }, fs);

        Assert.Equal("D50", found.Database!.Name);
        Assert.Equal(2, found.Database.FileCount);
        Assert.Equal(@"D:\Stars", found.DatabaseDirectory);
    }

    [Fact]
    public void FilesThatOnlyLookSimilar_AreNotADatabase()
    {
        var fs = FakeFileSystem.Installed(databaseName: null);
        fs.Folders[@"C:\Program Files\astap"].AddRange(["d50.txt", "d50_01.1476", "xd50_0101.1476", "variable_stars.csv"]);

        Assert.False(AstapLocator.Locate(new AstapConfiguration(), fs).HasDatabase);
    }

    [Fact]
    public void ABadConfiguration_SaysSo()
    {
        Assert.NotNull(new AstapConfiguration { SearchRadiusDegrees = 0 }.Problem());
        Assert.NotNull(new AstapConfiguration { SearchRadiusDegrees = 181 }.Problem());
        Assert.NotNull(new AstapConfiguration { Timeout = TimeSpan.Zero }.Problem());
        Assert.Null(new AstapConfiguration().Problem());
    }
}
