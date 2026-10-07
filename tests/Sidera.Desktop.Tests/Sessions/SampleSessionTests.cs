using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>The sample of a whole night (LRGB on M31, dusk to dawn, autofocus, dither and the meridian flip) is a session of the current format that compiles for an imaging setup with a wheel, a focuser and a guider.</summary>
public sealed class SampleSessionTests
{
    private static string SamplePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "samples", "LRGB-ganze-Nacht-M31.astraseq");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("The sample session was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task TheLrgbNight_IsASessionOfVersion9_ThatCompilesForOneSetup()
    {
        await using var equipment = SessionFixture.Create();
        var serializer = new JsonSequenceDocumentSerializer();
        await using var stream = File.OpenRead(SamplePath());

        var document = await serializer.LoadAsync(stream, CancellationToken.None);

        Assert.Null(document.Workflow);
        var session = Assert.IsType<SessionDefinition>(document.Session);
        var target = Assert.Single(session.Targets);
        Assert.Equal("M31 Andromeda Galaxy", target.Name);
        Assert.Equal([SessionActionKind.SlewAndCenter, SessionActionKind.StartGuiding], target.Preparation.Select(a => a.Kind));
        var lane = Assert.Single(target.Lanes);
        Assert.Null(lane.Setup); // one setup to image with: the sample names none
        Assert.True(lane.Blocks.Count >= 4);
        Assert.Contains(lane.Blocks, b => b.Automation.Dither is not null);
        Assert.Contains(lane.Blocks, b => b.Automation.Focus is { IsActive: true });
        Assert.False(session.Automation.UsesDefaultFlip);

        var compiled = SessionCompiler.Compile(session, equipment.Catalog);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
    }
}
