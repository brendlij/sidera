using Sidera.Core.Devices;
using Sidera.Core.Imaging;

namespace Sidera.Runtime.Tests.Imaging;

/// <summary>The names of the files that Sidera saves: the parts of a pattern, what is left out, and that a name is always one that a file system takes.</summary>
public class FrameNamingTests
{
    private static FrameNameValues Values(string? target = "M31", string? filter = "Ha", FrameType type = FrameType.Light, double seconds = 300, DateTime? start = null) =>
        new(target, "Newton", "CameraA", filter, type, seconds, start ?? new DateTime(2026, 10, 12, 22, 5, 9), 100, 50, -9.6, 1, 1);

    [Fact]
    public void TheDefaultPatterns_MakeTheTargetNightAndTypeFolders_AndAFileNameThatNamesTheFrame()
    {
        var values = Values();

        Assert.Equal(["M31", "2026-10-12", "Light"], FrameNaming.Folders(FrameNaming.DefaultFolderPattern, values));
        Assert.Equal("M31_Light_Ha_300s_0007", FrameNaming.FileName(FrameNaming.DefaultFilePattern, values, 7));
    }

    [Fact]
    public void EveryPart_HasAValue_AndTheWordingOfItsName()
    {
        var name = FrameNaming.FileName("{target}|{setup}|{camera}|{filter}|{type}|{exposure}|{night}|{date}|{time}|{n}|{gain}|{offset}|{temp}|{bin}", Values(), 12);

        Assert.Equal("M31|Newton|CameraA|Ha|Light|300s|2026-10-12|2026-10-12|220509|0012|gain100|offset50|-10C|bin1x1", name.Replace('_', '|'));
    }

    [Fact]
    public void ThePartsAreCaseInsensitive_AndEveryKnownPartIsDocumented()
    {
        Assert.Equal("M31", FrameNaming.FileName("{TARGET}", Values(), 1));
        Assert.All(FrameNaming.Parts, part => Assert.Null(FrameNaming.Problem("{" + part.Token + "}", isFile: true)));
        Assert.Equal(FrameNaming.Parts.Count, FrameNaming.Parts.Select(p => p.Token).Distinct().Count());
    }

    [Theory]
    [InlineData(22, "2026-10-12")]
    [InlineData(23, "2026-10-12")]
    public void TheNightIsTheEveningItBegan_ForAFrameBeforeMidnight(int hour, string night)
    {
        Assert.Equal(night, FrameNaming.Folders("{night}", Values(start: new DateTime(2026, 10, 12, hour, 0, 0)))[0]);
    }

    [Fact]
    public void TheNightIsTheEveningItBegan_ForAFrameAfterMidnight_ButTheDateIsTheNewDay()
    {
        var after = Values(start: new DateTime(2026, 10, 13, 1, 45, 30));

        Assert.Equal("2026-10-12", FrameNaming.Folders("{night}", after)[0]);
        Assert.Equal("2026-10-13", FrameNaming.Folders("{date}", after)[0]);
    }

    [Fact]
    public void APartThatIsNotKnown_IsLeftOut_WithTheSeparatorNextToIt()
    {
        var name = FrameNaming.FileName("{target}_{filter}_{exposure}_{n}", Values(filter: null), 3);

        Assert.Equal("M31_300s_0003", name);
        Assert.Equal(["2026-10-12", "Light"], FrameNaming.Folders("{target}/{night}/{type}", Values(target: null)));
    }

    [Fact]
    public void AFolderPartThatIsEmpty_MakesNoFolder_AndAnEmptyFolderPatternMakesNone()
    {
        Assert.Empty(FrameNaming.Folders("", Values()));
        Assert.Empty(FrameNaming.Folders(null, Values()));
        Assert.Equal(["Light"], FrameNaming.Folders("{filter}/{type}", Values(filter: " ")));
    }

    [Fact]
    public void ANameWithCharactersAFileSystemRefuses_IsMadeSafe_AndNeverLeavesItsFolder()
    {
        var name = FrameNaming.FileName("{target}", Values(target: "NGC 7000: North America?/..\\x"), 1);

        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('?', name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        // A target with a slash stays in one folder; a pattern with ".." makes no folder that climbs.
        Assert.Equal(["a-b"], FrameNaming.Folders("{target}", Values(target: "a/b")));
        Assert.DoesNotContain("..", FrameNaming.Folders("../../{target}", Values()));
    }

    [Fact]
    public void ALongName_IsCut_AndAnEmptyNameFallsBackToTheNumber()
    {
        Assert.True(FrameNaming.FileName("{target}", Values(target: new string('x', 400)), 1).Length <= 120);
        Assert.Equal("frame_0005", FrameNaming.FileName("{filter}", Values(filter: null), 5));
    }

    [Theory]
    [InlineData("{target}_{foo}", true, "unknown part {foo}")]
    [InlineData("{target", true, "brace")]
    [InlineData("target}", true, "brace")]
    [InlineData("", true, "needs a pattern")]
    [InlineData("a/b", true, "cannot hold a folder")]
    [InlineData("C:/frames", false, "inside the save folder")]
    [InlineData("{bogus}", false, "unknown part {bogus}")]
    public void APatternThatCannotBeUsed_SaysWhy(string pattern, bool isFile, string expected)
    {
        Assert.Contains(expected, FrameNaming.Problem(pattern, isFile));
    }

    [Fact]
    public void AnEmptyFolderPattern_IsAllowed_AndTheDefaultsAreValid()
    {
        Assert.Null(FrameNaming.Problem("", isFile: false));
        Assert.Null(FrameNaming.Problem(FrameNaming.DefaultFolderPattern, isFile: false));
        Assert.Null(FrameNaming.Problem(FrameNaming.DefaultFilePattern, isFile: true));
    }

    [Fact]
    public void TheFrameTypeIsInTheName_ForDarksFlatsAndBias()
    {
        Assert.Equal("Dark", FrameNaming.FileName("{type}", Values(type: FrameType.Dark), 1));
        Assert.Equal("Flat", FrameNaming.FileName("{type}", Values(type: FrameType.Flat), 1));
        Assert.Equal("Bias", FrameNaming.FileName("{type}", Values(type: FrameType.Bias), 1));
    }

    [Fact]
    public void TheExposureIsWrittenWithoutNoise_ForShortAndFractionalTimes()
    {
        Assert.Equal("0.5s", FrameNaming.FileName("{exposure}", Values(seconds: 0.5), 1));
        Assert.Equal("1.25s", FrameNaming.FileName("{exposure}", Values(seconds: 1.25), 1));
        Assert.Equal("60s", FrameNaming.FileName("{exposure}", Values(seconds: 60), 1));
    }
}
