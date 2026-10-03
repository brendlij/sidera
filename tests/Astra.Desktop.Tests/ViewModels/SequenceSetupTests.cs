using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;
using Astra.Runtime;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop.Tests.ViewModels;

public class SequenceSetupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Short timings, so that whole runs of the demo take about a second.</summary>
    private static readonly DemoOptions Fast = new()
    {
        ManualExposure = TimeSpan.FromMilliseconds(30),
        SequenceExposure = TimeSpan.FromMilliseconds(30),
        SequenceWait = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(20),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
    };

    private sealed class App : IAsyncDisposable
    {
        public required AstraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public SequenceSetupViewModel Setup => Vm.SequenceSetup;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }

        public async Task ConnectEverything()
        {
            await Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Mounts[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Guiders[0].ConnectCommand.ExecuteAsync(null);
        }
    }

    private static App Create(DemoOptions? options = null, Action<AstraRuntimeHost>? addMoreDevices = null)
    {
        var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, options ?? Fast);
        addMoreDevices?.Invoke(host);
        return new App { Host = host, Vm = new MainViewModel(host, action => action(), options ?? Fast) };
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    // The parts of a built demo sequence.
    private sealed record Parts(
        StartGuidingAction Start,
        SlewAction Slew,
        ParallelStep Parallel,
        RepeatStep ImagingRepeat,
        CameraExposureAction Exposure,
        RepeatStep DitherRepeat,
        DelayAction Delay,
        DitherAction Dither,
        StopGuidingAction Stop);

    private static Parts Dissect(Sequence sequence)
    {
        var parallel = Assert.IsType<ParallelStep>(sequence.Steps[2]);
        var imagingRepeat = Assert.IsType<RepeatStep>(Assert.IsType<SequenceGroup>(parallel.Children[0]).Children[0]);
        var imagingBlock = Assert.IsType<SequenceGroup>(imagingRepeat.Child);
        var ditherRepeat = Assert.IsType<RepeatStep>(Assert.IsType<SequenceGroup>(parallel.Children[1]).Children[0]);
        var ditherBlock = Assert.IsType<SequenceGroup>(ditherRepeat.Child);
        return new Parts(
            Assert.IsType<StartGuidingAction>(sequence.Steps[0]),
            Assert.IsType<SlewAction>(sequence.Steps[1]),
            parallel,
            imagingRepeat,
            Assert.IsType<CameraExposureAction>(imagingBlock.Children[0]),
            ditherRepeat,
            Assert.IsType<DelayAction>(ditherBlock.Children[0]),
            Assert.IsType<DitherAction>(ditherBlock.Children[1]),
            Assert.IsType<StopGuidingAction>(sequence.Steps[3]));
    }

    private static DeviceOption Option(IReadOnlyList<DeviceOption> options, string id) =>
        options.Single(o => o.IdText == id);

    // Defaults

    [Fact]
    public async Task DefaultConfiguration_ReproducesTheValuesOfTheFormerHardcodedDemo()
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);

        var c = DemoSequenceConfiguration.Default(new DemoOptions(), host.DeviceRegistry);

        Assert.Equal(new DeviceId("camera.main"), c.CameraId);
        Assert.Equal(new DeviceId("mount.eq6"), c.MountId);
        Assert.Equal(new DeviceId("guider.main"), c.GuiderId);
        Assert.Equal(5.588, c.RightAscensionHours);
        Assert.Equal(-5.39, c.DeclinationDegrees);
        Assert.Equal(2, c.ExposureSeconds);
        Assert.Equal(3, c.ImagingRepeatCount);
        Assert.Equal(2, c.DitherRepeatCount);
        Assert.Equal(2, c.DitherDelaySeconds);
        Assert.Equal(1.5, c.DitherAmplitudePixels);
        Assert.Equal(0.5, c.SettleThresholdPixels);
        Assert.Equal(1, c.SettleStableSeconds);
        Assert.Equal(10, c.SettleTimeoutSeconds);
    }

    [Fact]
    public async Task Editor_StartsWithTheDemoValuesAndTheDemoEquipmentSelected()
    {
        await using var app = Create(new DemoOptions());
        var setup = app.Setup;

        Assert.Equal("camera.main", setup.SelectedCamera?.IdText);
        Assert.Equal("Main Camera", setup.SelectedCamera?.Name);
        Assert.Equal("mount.eq6", setup.SelectedMount?.IdText);
        Assert.Equal("guider.main", setup.SelectedGuider?.IdText);
        Assert.Equal(
            new[] { "5.588", "-5.39", "2", "3", "2", "2", "1.5", "0.5", "1", "10" },
            new[]
            {
                setup.RightAscensionText, setup.DeclinationText, setup.ExposureText, setup.ImagingRepeatText,
                setup.DitherRepeatText, setup.DitherDelayText, setup.DitherAmplitudeText,
                setup.SettleThresholdText, setup.SettleStableText, setup.SettleTimeoutText,
            });
        Assert.True(setup.IsValid);
        Assert.Empty(setup.ValidationErrors);
    }

    [Fact]
    public async Task Selection_OffersOnlyCompatibleDevices_AndPicksTheFirstOneWhenTheDemoDevicesAreMissing()
    {
        await using var host = new AstraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.b"), "B camera");
        host.AddSimulatedCamera(new DeviceId("camera.a"), "A camera");
        host.AddSimulatedMount(new DeviceId("mount.x"), "X mount");

        var setup = new SequenceSetupViewModel(
            host.DeviceRegistry, DemoSequenceConfiguration.Default(new DemoOptions(), host.DeviceRegistry));

        Assert.Equal(new[] { "camera.a", "camera.b" }, setup.Cameras.Select(o => o.IdText));
        Assert.Equal(new[] { "mount.x" }, setup.Mounts.Select(o => o.IdText));
        Assert.Empty(setup.Guiders);
        Assert.Equal("camera.a", setup.SelectedCamera?.IdText);
        Assert.Contains("Select a guider.", setup.ValidationErrors);
        Assert.False(setup.IsValid);
    }

    // Validation

    [Theory]
    [InlineData(nameof(SequenceSetupViewModel.ExposureText), "0", "Exposure must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.ExposureText), "-2", "Exposure must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.ExposureText), "abc", "Exposure must be a number of seconds")]
    [InlineData(nameof(SequenceSetupViewModel.ExposureText), "", "Exposure must be a number of seconds")]
    [InlineData(nameof(SequenceSetupViewModel.ImagingRepeatText), "0", "Imaging repeat count must be at least 1")]
    [InlineData(nameof(SequenceSetupViewModel.ImagingRepeatText), "1.5", "Imaging repeat count must be a whole number")]
    [InlineData(nameof(SequenceSetupViewModel.DitherRepeatText), "0", "Dither repeat count must be at least 1")]
    [InlineData(nameof(SequenceSetupViewModel.RightAscensionText), "24", "Right ascension")]
    [InlineData(nameof(SequenceSetupViewModel.RightAscensionText), "-1", "Right ascension")]
    [InlineData(nameof(SequenceSetupViewModel.RightAscensionText), "x", "Right ascension must be a number of hours")]
    [InlineData(nameof(SequenceSetupViewModel.DeclinationText), "91", "Declination")]
    [InlineData(nameof(SequenceSetupViewModel.DeclinationText), "-91", "Declination")]
    [InlineData(nameof(SequenceSetupViewModel.DitherAmplitudeText), "0", "Dither amplitude must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.DitherDelayText), "0", "Wait before dither must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.SettleThresholdText), "0", "Settle threshold must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.SettleStableText), "0", "Settle stable time must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.SettleTimeoutText), "0", "Settle timeout must be greater than 0")]
    [InlineData(nameof(SequenceSetupViewModel.SettleTimeoutText), "0.05", "Settle timeout must be longer than the stable time")]
    public async Task InvalidValue_ShowsAMessage_AndDisablesRun(string property, string text, string expected)
    {
        await using var app = Create();
        await app.ConnectEverything();
        Assert.True(app.Sequencer.RunCommand.CanExecute(null));

        typeof(SequenceSetupViewModel).GetProperty(property)!.SetValue(app.Setup, text);

        Assert.False(app.Setup.IsValid);
        Assert.Contains(app.Setup.ValidationErrors, error => error.Contains(expected));
        Assert.All(app.Setup.ValidationErrors, error => Assert.DoesNotContain("   at ", error)); // no stack traces
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));
        Assert.False(app.Sequencer.CanRun);
    }

    [Fact]
    public async Task RestoringAValidValue_EnablesRunAgain()
    {
        await using var app = Create();
        await app.ConnectEverything();

        app.Setup.ExposureText = "0";
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));
        app.Setup.ExposureText = "0.5";

        Assert.True(app.Setup.IsValid);
        Assert.True(app.Sequencer.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task MissingSelections_DisableRun()
    {
        await using var app = Create();
        await app.ConnectEverything();

        app.Setup.SelectedCamera = null;
        Assert.Contains("Select a camera.", app.Setup.ValidationErrors);
        app.Setup.SelectedCamera = Option(app.Setup.Cameras, "camera.main");
        app.Setup.SelectedMount = null;
        Assert.Contains("Select a mount.", app.Setup.ValidationErrors);
        app.Setup.SelectedMount = Option(app.Setup.Mounts, "mount.eq6");
        app.Setup.SelectedGuider = null;
        Assert.Contains("Select a guider.", app.Setup.ValidationErrors);
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADeviceThatDisappears_MakesTheConfigurationInvalid_WithoutCrashing()
    {
        await using var app = Create();
        await app.ConnectEverything();
        Assert.True(app.Sequencer.RunCommand.CanExecute(null));

        app.Host.DeviceRegistry.Unregister(new DeviceId("guider.main"));
        app.Sequencer.RefreshReadiness();

        Assert.False(app.Setup.IsValid);
        Assert.Contains(app.Setup.ValidationErrors, e => e.Contains("'guider.main' is not available"));
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));
    }

    // Builder

    [Fact]
    public async Task Builder_UsesTheSelectedDevicesAndTheEditedValues()
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        host.AddSimulatedCamera(new DeviceId("camera.wide"), "Wide");
        host.AddSimulatedMount(new DeviceId("mount.az"), "AZ");
        host.AddSimulatedGuider(new DeviceId("guider.alt"), "Alt guider");
        var configuration = new DemoSequenceConfiguration
        {
            CameraId = new DeviceId("camera.wide"),
            MountId = new DeviceId("mount.az"),
            GuiderId = new DeviceId("guider.alt"),
            RightAscensionHours = 12.34,
            DeclinationDegrees = 45.2,
            ExposureSeconds = 300,
            ImagingRepeatCount = 20,
            DitherRepeatCount = 7,
            DitherDelaySeconds = 4.5,
            DitherAmplitudePixels = 3,
            SettleThresholdPixels = 0.7,
            SettleStableSeconds = 2.5,
            SettleTimeoutSeconds = 30,
        };

        var parts = Dissect(DemoSequenceBuilder.Build(host.DeviceRegistry, configuration));

        Assert.Equal(new DeviceId("camera.wide"), parts.Exposure.CameraId);
        Assert.Equal(TimeSpan.FromSeconds(300), parts.Exposure.Duration);
        Assert.Equal(20, parts.ImagingRepeat.Count);
        Assert.Equal(new[] { ResourceId.ForDevice(new DeviceId("mount.az")) }, parts.Slew.RequiredResources);
        Assert.Equal(12.34, parts.Slew.Target.RightAscensionHours);
        Assert.Equal(45.2, parts.Slew.Target.DeclinationDegrees);
        var guider = new[] { ResourceId.ForDevice(new DeviceId("guider.alt")) };
        Assert.Equal(guider, parts.Start.RequiredResources);
        Assert.Equal(guider, parts.Stop.RequiredResources);
        Assert.Equal(7, parts.DitherRepeat.Count);
        Assert.Equal(TimeSpan.FromSeconds(4.5), parts.Delay.Duration);
        Assert.Equal(new DeviceId("guider.alt"), parts.Dither.GuiderId);
        Assert.Equal(new DeviceId("mount.az"), parts.Dither.MountId);
        Assert.Equal(new[] { new DeviceId("camera.wide") }, parts.Dither.CameraIds);
        Assert.Equal(3, parts.Dither.AmplitudePixels);
        Assert.Equal(0.7, parts.Dither.SettleOptions!.MaximumErrorPixels);
        Assert.Equal(TimeSpan.FromSeconds(2.5), parts.Dither.SettleOptions.StableDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), parts.Dither.SettleOptions.Timeout);

        // The coordination is part of the template: the user never configures it.
        Assert.Equal(DemoSetup.CoordinationGroup, parts.Parallel.CoordinationGroup);
    }

    [Fact]
    public async Task Builder_BuildsAFreshTreeEveryTime()
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        var configuration = DemoSequenceConfiguration.Default(new DemoOptions(), host.DeviceRegistry);

        var first = Dissect(DemoSequenceBuilder.Build(host.DeviceRegistry, configuration));
        var second = Dissect(DemoSequenceBuilder.Build(host.DeviceRegistry, configuration));

        Assert.NotSame(first.Exposure, second.Exposure);
        Assert.NotSame(first.Dither, second.Dither);
        Assert.NotSame(first.Parallel, second.Parallel);
    }

    [Fact]
    public async Task Builder_RefusesAnInvalidConfiguration_WithShortProblems()
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        var bad = DemoSequenceConfiguration.Default(new DemoOptions(), host.DeviceRegistry) with
        {
            ExposureSeconds = 0,
            ImagingRepeatCount = 0,
            GuiderId = null,
        };

        var error = Assert.Throws<SequenceConfigurationException>(() => DemoSequenceBuilder.Build(host.DeviceRegistry, bad));

        Assert.Contains("Exposure must be greater than 0 s.", error.Problems);
        Assert.Contains("Imaging repeat count must be at least 1.", error.Problems);
        Assert.Contains("Select a guider.", error.Problems);
    }

    [Fact]
    public async Task Builder_RefusesAWrongKindOfDevice()
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        var bad = DemoSequenceConfiguration.Default(new DemoOptions(), host.DeviceRegistry) with
        {
            CameraId = new DeviceId("mount.eq6"),
        };

        var error = Assert.Throws<SequenceConfigurationException>(() => DemoSequenceBuilder.Build(host.DeviceRegistry, bad));

        Assert.Contains("'mount.eq6' is not a camera.", error.Problems);
    }

    // Workflow preview

    [Fact]
    public async Task Preview_ReflectsTheEditedValues_BeforeRunning()
    {
        await using var app = Create(new DemoOptions());
        var setup = app.Setup;

        setup.ExposureText = "300";
        setup.ImagingRepeatText = "20";
        setup.RightAscensionText = "12.34";
        setup.DeclinationText = "45.2";
        setup.DitherAmplitudeText = "3";
        setup.SettleThresholdText = "0.7";
        setup.SettleStableText = "2.5";
        setup.SettleTimeoutText = "30";

        var definition = app.Sequencer.Definition;
        Assert.Equal("300 s", definition.Single(n => n.Title == "Exposure").Detail);
        Assert.Contains(definition, n => n.Title == "Repeat" && n.Detail == "×20");
        Assert.Equal("RA 12.34 h · Dec +45.2°", definition.Single(n => n.Title == "Slew").Detail);
        var dither = definition.Single(n => n.Title == "Dither" && n.Kind == SequenceNodeKind.Step);
        Assert.Equal("3 px", dither.Detail);
        Assert.Equal("then settle ≤ 0.7 px for 2.5 s", dither.SubText);
    }

    [Fact]
    public async Task Preview_KeepsTheLastValidDefinition_WhileTheInputIsInvalid()
    {
        await using var app = Create(new DemoOptions());
        app.Setup.ExposureText = "10";
        var shown = app.Sequencer.Definition;

        app.Setup.ExposureText = "1e"; // a partial entry
        Assert.False(app.Setup.IsValid);
        Assert.Same(shown, app.Sequencer.Definition);

        app.Setup.ExposureText = "12";
        Assert.Equal("12 s", app.Sequencer.Definition.Single(n => n.Title == "Exposure").Detail);
    }

    // Running

    [Fact]
    public async Task Run_BuildsFromTheLatestValues()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ImagingRepeatText = "1";   // the default would take three frames
        app.Setup.DitherRepeatText = "1";
        app.Setup.ExposureText = "0.04";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Null(app.Sequencer.ErrorMessage);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.04), app.Vm.Imaging.LatestFrame!.ExposureDuration);
        Assert.Contains(app.Sequencer.Definition, n => n.Title == "Repeat" && n.Detail == "×1");
    }

    [Fact]
    public async Task EditingAfterTheRunHasStarted_DoesNotChangeTheRunningSequence()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ExposureText = "0.4";
        app.Setup.ImagingRepeatText = "1";
        app.Setup.DitherRepeatText = "1";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");

        app.Setup.ExposureText = "9";        // the editors are disabled, but even a stray change must not reach the run
        app.Setup.ImagingRepeatText = "5";

        Assert.Equal("0.4 s", app.Sequencer.Definition.Single(n => n.Title == "Exposure").Detail);
        Assert.Contains(app.Sequencer.Definition, n => n.Title == "Repeat" && n.Detail == "×1");
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.4), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    [Fact]
    public async Task Run_WithAnInvalidConfiguration_DoesNotStartTheRunner_AndStaysEditable()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ExposureText = "0";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound); // bypassing the disabled button

        Assert.Equal(SequenceState.Idle, app.Sequencer.State);
        Assert.Equal("Exposure must be greater than 0 s.", app.Sequencer.ErrorMessage);
        Assert.True(app.Setup.IsEditable);
        Assert.Equal(0, app.Vm.Imaging.FrameCount);
    }

    // Locking

    [Fact]
    public async Task Editors_AreLockedWhileTheRunIsRunningPausingOrPaused_AndFreeAgainAfterwards()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ExposureText = "0.3";
        app.Setup.ImagingRepeatText = "2";
        Assert.True(app.Setup.IsEditable);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        Assert.False(app.Setup.IsEditable);

        app.Sequencer.PauseCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Pausing, "pausing");
        Assert.False(app.Setup.IsEditable);

        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");
        Assert.False(app.Setup.IsEditable);

        // Resume uses the sequence that was built at the start.
        app.Sequencer.ResumeCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running again");
        Assert.False(app.Setup.IsEditable);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.True(app.Setup.IsEditable);
    }

    [Fact]
    public async Task Editors_AreFreeAgainAfterACancelledRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ExposureText = "0.3";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        Assert.False(app.Setup.IsEditable);
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        Assert.True(app.Setup.IsEditable);
    }

    [Fact]
    public async Task Editors_AreFreeAgainAfterAFailedRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        // The guide error can never get below this threshold within the timeout: the settle wait fails.
        app.Setup.SettleThresholdText = "0.31";
        app.Setup.SettleStableText = "0.1";
        app.Setup.SettleTimeoutText = "0.3";
        app.Setup.DitherAmplitudeText = "5";
        app.Setup.ImagingRepeatText = "2";
        app.Setup.DitherRepeatText = "1";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.Equal("Guiding did not settle within the time limit.", app.Sequencer.ErrorMessage);
        Assert.True(app.Setup.IsEditable);

        // The cause can be fixed and the sequence run again.
        app.Setup.DitherAmplitudeText = "0.6";
        app.Setup.SettleTimeoutText = "5";
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
    }

    // Regression of the buttons

    [Fact]
    public async Task PauseResumeAndCancel_StillFollowTheState_WithTheSetupInPlace()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Setup.ExposureText = "0.3";
        var vm = app.Sequencer;
        Assert.Equal((true, false, false, false), Buttons(vm));

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.Equal((false, true, false, true), Buttons(vm));

        vm.PauseCommand.Execute(null);
        Assert.Equal((false, false, false, true), Buttons(vm));
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, true, true), Buttons(vm));

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    private static (bool Run, bool Pause, bool Resume, bool Cancel) Buttons(SequencerViewModel vm) =>
        (vm.RunCommand.CanExecute(null), vm.PauseCommand.CanExecute(null),
         vm.ResumeCommand.CanExecute(null), vm.CancelCommand.CanExecute(null));
}
