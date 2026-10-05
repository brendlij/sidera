using Sidera.Ascom.Discovery;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Rotators;
using Sidera.Core;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Runtime;
using Xunit.Abstractions;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// The rotator through the real ASCOM COM layer: against the ASCOM rotator simulator (<c>SIDERA_ASCOM_TESTS=1</c>), and, read-only, against a real rotator
/// (<c>SIDERA_ASCOM_ROTATOR=&lt;ProgId&gt;</c>). A real rotator moves only with <c>SIDERA_ASCOM_ROTATOR_OK=1</c>, by two degrees and back.
/// </summary>
public sealed class RotatorSimulatorTests(ITestOutputHelper output)
{
    private const string SimulatorProgId = "ASCOM.Simulator.Rotator";
    private static readonly ComAscomDriverFactory Drivers = new();

    /// <summary>Reports the rotation of the sky as it would be seen by a camera on the rotator that is mounted with a given offset (and not reversed).</summary>
    private sealed class RotatorSolver(IRotator rotator, double offset) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlateSolveResult
            {
                Success = true, Center = request.ApproximateCenter ?? new Sidera.Core.Mounts.CelestialCoordinates(1, 1),
                RotationDegrees = SkyMath.NormalizeRotationDegrees(rotator.Position + offset), Backend = Name,
            });
    }

    private AscomRotator NewSimulator() => new(new DeviceId("rotator.sim"), "Rotator Simulator", SimulatorProgId, Drivers);

    [AscomFact]
    public async Task Discovery_FindsTheRotatorSimulator()
    {
        var found = await new AscomDiscovery().DiscoverAsync(AscomDeviceKind.Rotator);

        Assert.True(found.PlatformAvailable);
        Assert.Null(found.Problem);
        Assert.Contains(found.Drivers, d => d.ProgId == SimulatorProgId);
    }

    [AscomFact]
    public async Task TheRotatorSimulator_Connects_ReportsWhatItCan_AndMovesNothingByItself()
    {
        var rotator = NewSimulator();
        Assert.Equal(DeviceConnectionState.Disconnected, rotator.ConnectionState);

        await rotator.ConnectAsync();
        try
        {
            var before = rotator.Position;
            var c = rotator.Capabilities.Value!;
            output.WriteLine($"position {before} mechanical {rotator.MechanicalPosition} absolute {c.AbsoluteMove} relative {c.RelativeMove} sync {c.CanSync} reverse {c.CanReverse} step {c.StepSizeDegrees}");
            Assert.True(c.AbsoluteMove);
            Assert.InRange(before, 0, 360);
            await Task.Delay(500);
            await rotator.RefreshAsync();
            Assert.Equal(before, rotator.Position);
            Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        }
        finally
        {
            await rotator.DisconnectAsync();
        }

        Assert.Equal(DeviceConnectionState.Disconnected, rotator.ConnectionState);
    }

    [AscomFact]
    public async Task TheRotatorSimulator_MovesAbsoluteAndRelative_AndCanBeHalted()
    {
        var rotator = NewSimulator();
        await rotator.ConnectAsync();
        try
        {
            var start = rotator.Position;

            await rotator.MoveToAsync(RotatorSkyModel.Normalize360(start + 20));
            Assert.Equal(RotatorSkyModel.Normalize360(start + 20), rotator.Position, 0.5);

            if (rotator.Capabilities.Value!.RelativeMove)
            {
                await rotator.MoveByAsync(-10);
                Assert.Equal(RotatorSkyModel.Normalize360(start + 10), rotator.Position, 0.5);
            }

            using var cts = new CancellationTokenSource();
            var move = rotator.MoveToAsync(RotatorSkyModel.Normalize360(start + 150), cts.Token);
            await Task.Delay(300);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            await Task.Delay(300);
            await rotator.RefreshAsync();
            Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
            Assert.NotEqual(150, RotatorSkyModel.Normalize360(rotator.Position - start), 1);

            await rotator.MoveToAsync(start);
            Assert.Equal(start, rotator.Position, 0.5);
        }
        finally
        {
            await rotator.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task RotateAndVerify_OnTheRotatorSimulator_ReachesTheSkyAngle_ThroughTheHost()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        var rotator = new AscomRotator(new DeviceId("rotator.sim"), "Rotator Simulator", SimulatorProgId, Drivers, host.EventBus);
        host.AddDevice(rotator);
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        await host.DeviceOperations.ConnectAsync(rotator.Id);
        try
        {
            // The sky is 30 degrees further round than the rig believes: the verification finds out and corrects it.
            var start = rotator.Position;
            var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76), rotatorId: rotator.Id, rotatorModel: new RotatorSkyModel(-start));
            host.AddRig(rig);
            host.ConfigurePlateSolver(new RotatorSolver(rotator, -start + 30));

            var result = await host.Rotation!.RotateAndVerifyAsync(rig, mount.Id, 100, 0.5, 4, TimeSpan.FromMilliseconds(1), new PlateSolveDefaults());

            output.WriteLine($"{result.Success} after {result.Attempts} attempts: position {result.PositionDegrees}, sky {result.SolvedRotationDegrees}, error {result.ErrorDegrees} {result.Message}");
            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Attempts);
            Assert.InRange(Math.Abs(SkyMath.RotationDifferenceDegrees(result.SolvedRotationDegrees!.Value, 100)), 0, 0.5);
            Assert.False(host.Rotation.IsBusy);
        }
        finally
        {
            await rotator.MoveToAsync(0).WaitAsync(TimeSpan.FromSeconds(60));
            await host.DeviceOperations.DisconnectAsync(rotator.Id);
        }
    }

    // ---- A real rotator

    private static string Env(string name) => SideraEnvironment.Get(name) ?? string.Empty;

    private AscomRotator NewRealRotator() => new(new DeviceId("rotator.real"), "Real Rotator", Env("SIDERA_ASCOM_ROTATOR"), Drivers);

    [HardwareFact("SIDERA_ASCOM_ROTATOR")]
    public async Task TheRealRotator_Telemetry_ThroughSidera_ReadOnly()
    {
        var rotator = NewRealRotator();
        await rotator.ConnectAsync();
        try
        {
            await rotator.RefreshAsync();
            var c = rotator.Capabilities.Value!;
            output.WriteLine($"position {rotator.Position} mechanical {rotator.MechanicalPosition} moving {rotator.MotionState} absolute {c.AbsoluteMove} relative {c.RelativeMove} sync {c.CanSync} reverse {c.CanReverse} step {c.StepSizeDegrees}");
            Assert.InRange(rotator.Position, 0, 360);
            Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        }
        finally
        {
            await rotator.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_ROTATOR")]
    public async Task TheRealRotator_MovesTwoDegreesAndBack_OnlyWithItsGate()
    {
        if (Env("SIDERA_ASCOM_ROTATOR_OK") != "1")
        {
            output.WriteLine("SIDERA_ASCOM_ROTATOR_OK=1 is not set: the rotator is not moved.");
            return;
        }

        var rotator = NewRealRotator();
        await rotator.ConnectAsync();
        var start = rotator.Position;
        try
        {
            await rotator.MoveToAsync(RotatorSkyModel.Normalize360(start + 2)).WaitAsync(TimeSpan.FromSeconds(120));
            output.WriteLine($"moved from {start} to {rotator.Position}");
            Assert.Equal(RotatorSkyModel.Normalize360(start + 2), rotator.Position, 0.5);
        }
        finally
        {
            await rotator.MoveToAsync(start).WaitAsync(TimeSpan.FromSeconds(120)); // back where it was
            output.WriteLine($"back at {rotator.Position}");
            await rotator.DisconnectAsync();
        }
    }
}
