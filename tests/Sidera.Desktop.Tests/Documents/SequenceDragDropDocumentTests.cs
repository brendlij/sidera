using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>Reordering by drag and drop together with the document: it is a change, it is saved, and it comes back in the same order.</summary>
public sealed class SequenceDragDropDocumentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-drag-tests-" + Guid.NewGuid().ToString("N"));

    public SequenceDragDropDocumentTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private sealed class Picker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult(SavePath);
    }

    private sealed class App(SideraRuntimeHost host, MainViewModel vm, Picker picker) : IAsyncDisposable
    {
        public MainViewModel Vm { get; } = vm;
        public SequenceDocumentViewModel Document => Vm.SequenceDocument;
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await host.DisposeAsync();
        }

        public async Task SaveAs(string path)
        {
            picker.SavePath = path;
            await Document.SaveAsCommand.ExecuteAsync(null);
        }

        public async Task Open(string path)
        {
            picker.OpenPath = path;
            await Document.OpenCommand.ExecuteAsync(null);
            if (Document.IsConfirmingDiscard)
            {
                await Document.ConfirmDiscardCommand.ExecuteAsync(null);
            }
        }
    }

    private static App Create()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var picker = new Picker();
        return new App(host, new MainViewModel(host, a => a(), new DemoOptions(), SequenceDocumentStore.CreateDefault(), picker), picker);
    }

    private static List<Guid> Everything(SequenceDraftViewModel draft) => draft.Rows.Select(r => r.Id).ToList();

    // Slew, Exposure, Repeat × 3 [Exposure, Delay, Slew], Multi-Rig [Main: RigExposure, Delay; Wide: RigExposure].
    private sealed record Fixture(
        SequenceStepDraft[] Top, LeafStepDraft[] Inner, RigTrackDraft Main, RigTrackDraft Wide, RigExposureStepDraft TrackStep, DelayStepDraft TrackDelay);

    private static Fixture Fill(SequenceDraftViewModel draft)
    {
        LeafStepDraft[] inner =
        [
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1),
            new DelayStepDraft(Guid.NewGuid(), 1),
            new SlewStepDraft(Guid.NewGuid(), DemoSetup.MountId, 3, 4),
        ];
        var trackStep = new RigExposureStepDraft(Guid.NewGuid(), 0.1);
        var trackDelay = new DelayStepDraft(Guid.NewGuid(), 2);
        var main = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.main"), [trackStep, trackDelay]);
        var wide = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.wide"), [new RigExposureStepDraft(Guid.NewGuid(), 0.2)]);
        SequenceStepDraft[] top =
        [
            new SlewStepDraft(Guid.NewGuid(), DemoSetup.MountId, 5.5, -5),
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.2),
            new RepeatStepDraft(Guid.NewGuid(), 3, inner),
            new MultiRigStepDraft(Guid.NewGuid(), [main, wide]),
        ];
        draft.ReplaceSteps(top);
        return new Fixture(top, inner, main, wide, trackStep, trackDelay);
    }

    [Fact]
    public async Task AReorderingByDragAndDrop_MarksTheDocumentModified_IsSaved_AndComesBackInTheSameOrder()
    {
        await using var app = Create();
        var f = Fill(app.Draft);
        await app.SaveAs(PathOf("Order"));
        Assert.False(app.Document.IsDirty);

        // The Exposure above the Slew; the last step of the Repeat to its head; the delay of the track to its head; the Wide track first.
        Assert.True(app.Draft.Drop(f.Top[1].Id, f.Top[0].Id, DropPlacement.Before));
        Assert.True(app.Document.IsDirty);
        Assert.True(app.Draft.Drop(f.Inner[2].Id, f.Inner[0].Id, DropPlacement.Before));
        Assert.True(app.Draft.Drop(f.TrackDelay.Id, f.TrackStep.Id, DropPlacement.Before));
        Assert.True(app.Draft.Drop(f.Wide.Id, f.Main.Id, DropPlacement.Before));
        var order = Everything(app.Draft);

        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.False(app.Document.IsDirty);
        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);
        Assert.True(app.Draft.IsEmpty);
        await app.Open(PathOf("Order.astraseq"));

        Assert.Equal(order, Everything(app.Draft)); // every step, in the order of the rows, with the ids it had
        Assert.Equal([f.Top[1].Id, f.Top[0].Id, f.Top[2].Id, f.Top[3].Id], app.Draft.Steps.Select(s => s.Id));
        Assert.Equal(
            [f.Inner[2].Id, f.Inner[0].Id, f.Inner[1].Id], ((ContainerStepDraftViewModel)app.Draft.Steps[2]).Children.Select(c => c.Id));
        var tracks = ((ContainerStepDraftViewModel)app.Draft.Steps[3]).Children;
        Assert.Equal([f.Wide.Id, f.Main.Id], tracks.Select(t => t.Id));
        Assert.Equal([f.TrackDelay.Id, f.TrackStep.Id], ((ContainerStepDraftViewModel)tracks[1]).Children.Select(c => c.Id));
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task ARefusedOrCancelledDrop_LeavesTheDocumentClean()
    {
        await using var app = Create();
        var f = Fill(app.Draft);
        await app.SaveAs(PathOf("Clean"));

        Assert.True(app.Draft.BeginDrag(f.Top[0].Id));
        app.Draft.EndDrag(); // cancelled
        Assert.False(app.Draft.Drop(f.Top[0].Id, f.Top[0].Id, DropPlacement.After)); // where it is
        Assert.False(app.Draft.Drop(f.Top[1].Id, f.Inner[0].Id, DropPlacement.Before)); // into the Repeat
        Assert.False(app.Draft.Drop(f.TrackStep.Id, f.Top[0].Id, DropPlacement.Before)); // a rig step to the top level
        Assert.False(app.Draft.Drop(f.Top[2].Id, f.Inner[1].Id, DropPlacement.After)); // the Repeat into itself

        Assert.False(app.Document.IsDirty);
        Assert.Equal(f.Top.Select(s => s.Id), app.Draft.Steps.Select(s => s.Id));
    }
}
