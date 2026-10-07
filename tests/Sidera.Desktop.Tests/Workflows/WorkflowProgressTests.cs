using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>The progress of a running workflow is shown block by block: the blocks of one setup run one after another, so only one of them is making frames.</summary>
public sealed class WorkflowProgressTests : IAsyncLifetime
{
    private SideraRuntimeHost? _host;
    private MainViewModel? _vm;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _vm?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task BlocksOfOneSetup_ShowOneBlockAtWork_TheDoneOnesDone_AndTheRestQueued()
    {
        _host = new SideraRuntimeHost();
        _host.AddSimulatedCamera(new("camera.only"), "Only camera", 1);
        _host.DeviceRegistry.TryGet(new DeviceId("camera.only"), out var camera);
        await camera!.ConnectAsync();
        _vm = new MainViewModel(_host, a => a(), new DemoOptions());
        var editor = _vm.Workflow;
        editor.Load(WorkflowDefinition.Empty with
        {
            Imaging =
            [
                new ImagingBlock(Guid.NewGuid(), null, null, 0.2, 3),
                new ImagingBlock(Guid.NewGuid(), null, null, 0.2, 3),
                new ImagingBlock(Guid.NewGuid(), null, null, 0.2, 3),
            ],
        });
        Assert.True(_vm.SequenceDraft.IsValid, string.Join(" ", _vm.SequenceDraft.ValidationErrors));

        _vm.Sequencer.RunCommand.Execute(null);
        var sawQueuedBehindAFrame = false;
        var sawDoneBeforeNext = false;
        var maxAtWork = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (_vm.Sequencer.State is SequenceState.Running or SequenceState.Idle)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            var texts = editor.ImagingRows.Select(r => r.ProgressText).ToList();
            maxAtWork = Math.Max(maxAtWork, texts.Count(t => t.StartsWith("Frame", StringComparison.Ordinal)));
            sawQueuedBehindAFrame |= texts[0].StartsWith("Frame", StringComparison.Ordinal) && texts[1] == "Queued" && texts[2] == "Queued";
            sawDoneBeforeNext |= texts[0] == "3 / 3 frames" && (texts[1].StartsWith("Frame", StringComparison.Ordinal) || texts[1] == "Starting") && texts[2] == "Queued";
            await Task.Delay(5);
        }

        Assert.Equal(SequenceState.Completed, _vm.Sequencer.State);
        Assert.Equal(1, maxAtWork); // never two blocks of one setup at once
        Assert.True(sawQueuedBehindAFrame, "the first block makes frames while the others are queued");
        Assert.True(sawDoneBeforeNext, "a block that is done says so while the next one is at work");
    }
}
