using System.Text.RegularExpressions;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>What the normal user interface says and what it does not: no rigs, tracks or bindings; nothing about several setups with one camera; the meridian flip under Session Automation and the dither under Block Automation.</summary>
public sealed class NormalUiWordingTests
{
    private const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

    private static readonly Regex Attribute = new(@"\b(Text|Content|Header|Label|Title|Message|ToolTip\.Tip|PlaceholderText|AutomationProperties\.Name)=""([^""{]*)""", RegexOptions.Compiled);
    private static readonly Regex Technical = new(@"\b(Rigs?|Tracks?|Standalone|DeviceId|SetupId|BindingId|Pointing setup|Frames counted on|Mount group|Resource group)\b", RegexOptions.Compiled);

    private static string SourceOfTheApplication()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sidera.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Sidera.Desktop");
            }
        }

        throw new DirectoryNotFoundException("The source of the application was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void NoViewSays_Rig_Track_Standalone_OrABindingOrADeviceId()
    {
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceOfTheApplication(), "*.axaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match match in Attribute.Matches(line))
                {
                    if (Technical.IsMatch(match.Groups[2].Value))
                    {
                        found.Add($"{Path.GetFileName(file)}:{lineNumber} {match.Groups[2].Value}");
                    }
                }
            }
        }

        Assert.Empty(found);
    }

    [Fact]
    public async Task WithOneCamera_TheSessionHasNoLanes_NoSetupChoice_AndNothingShared()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var editor = app.Vm.SessionEditor;
        editor.Load(SessionDefinition.Empty);

        editor.AddTargetCommand.Execute(null);

        var target = editor.Targets[0];
        Assert.False(editor.IsMultiSetup);
        Assert.False(target.ShowLaneTabs);
        Assert.Equal(string.Empty, target.ParallelText);
        Assert.Equal(string.Empty, target.SharedText);
        Assert.Equal("Imaging", target.Lanes[0].SetupName); // not the name of a setup, and not "Track A"
        Assert.False(target.Lanes[0].NeedsSetupChoice);
        Assert.False(editor.HasSetupNotice);
        Assert.False(app.Vm.SetupContext.HasSeveral); // the sidebar has nothing to choose
        Assert.DoesNotContain(editor.Problems, problem => problem.Contains("imaging setup", StringComparison.OrdinalIgnoreCase)); // what is missing is a plate solver, not a choice of setups
    }

    [Fact]
    public async Task WithTwoSetups_TheSessionShowsAnImagingSetupAsATab_AndWhatTheyShare()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var editor = app.Vm.SessionEditor;
        editor.Load(SessionDefinition.Empty);
        editor.AddTargetCommand.Execute(null);
        editor.Targets[0].AddLaneCommand.Execute(null);

        var target = editor.Targets[0];
        Assert.True(target.ShowLaneTabs);
        Assert.Equal(2, target.Lanes.Count);
        Assert.All(target.Lanes, lane => Assert.DoesNotContain("Track", lane.SetupName, StringComparison.Ordinal));
        Assert.StartsWith("Parallel imaging · ", target.ParallelText, StringComparison.Ordinal);
        Assert.StartsWith("Shared: ", target.SharedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMeridianFlipIsUnderSessionAutomation_AndTheDitherUnderTheAutomationOfABlock()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        var editor = app.Vm.SessionEditor;
        editor.Load(SessionDefinition.Empty);
        editor.AddTargetCommand.Execute(null);

        Assert.NotNull(editor.Automation);
        Assert.StartsWith("Off", editor.Automation!.FlipLine, StringComparison.Ordinal); // the flip is there, once, for the session
        Assert.Equal(["Flip", "UsesDefaultFlip"], typeof(SessionAutomation).GetProperties(Instance).Select(p => p.Name).Order()); // and nothing else of the session is automation yet
        Assert.Equal(["Dither", "Focus", "IsEmpty"], typeof(BlockAutomation).GetProperties(Instance).Select(p => p.Name).Order()); // the dither and the focus are the block's
        editor.SelectBlock(editor.Session!.Targets[0].Lanes[0].Blocks[0].Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        Assert.True(drawer.CanDither);
    }
}
