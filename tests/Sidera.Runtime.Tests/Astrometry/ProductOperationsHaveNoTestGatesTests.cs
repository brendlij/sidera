using Sidera.Core;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Astrometry;

/// <summary>
/// Plate solving and centering are product operations: they run on any camera and mount without an environment variable. The variables of the hardware tests (SIDERA_ASTAP_CAMERA_OK,
/// SIDERA_ASTROMETRY_CENTERING_OK) once blocked them in the service; what asks before equipment moves is the application, with a confirmation the user answers.
/// </summary>
public sealed class ProductOperationsHaveNoTestGatesTests
{
    private static readonly CelestialCoordinates Target = new(5, 30);

    private static (SideraRuntimeHost Host, Rig Rig, FakeMount Mount, FakePlateSolver Solver) Create()
    {
        // Neither variable is set for a normal run of the application, and these tests do not set them.
        Assert.Null(SideraEnvironment.Get("SIDERA_ASTAP_CAMERA_OK"));
        Assert.Null(SideraEnvironment.Get("SIDERA_ASTROMETRY_CENTERING_OK"));
        var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera");
        var mount = new FakeMount("mount");
        host.AddDevice(camera);
        host.AddDevice(mount);
        camera.ConnectAsync().GetAwaiter().GetResult();
        var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76));
        host.AddRig(rig);
        var solver = new FakePlateSolver();
        host.ConfigurePlateSolver(solver);
        return (host, rig, mount, solver);
    }

    [Fact]
    public async Task ASolveExposureOfARealCamera_IsNotBlocked()
    {
        var (host, rig, mount, solver) = Create();
        await using var _ = host;
        solver.Success(Target);

        var result = await host.PlateSolving!.CaptureAndSolveAsync(rig, mount.Id, TimeSpan.FromMilliseconds(1), new PlateSolveDefaults());

        Assert.True(result.Success, result.Message);
        Assert.Single(solver.Requests);
        Assert.DoesNotContain("SIDERA_", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CenteringARealMount_IsNotBlocked()
    {
        var (host, rig, mount, solver) = Create();
        await using var _ = host;
        solver.Success(Target);

        var result = await host.PlateSolving!.CenterTargetAsync(Target, rig, mount.Id, 60, 3, TimeSpan.FromMilliseconds(1), new PlateSolveDefaults());

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, mount.SlewCalls); // it slewed: nothing stopped it
        Assert.DoesNotContain("SIDERA_", result.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceSource_NamesNoEnvironmentVariable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Sidera.Runtime", "Astrometry", "PlateSolveService.cs");
        Assert.True(File.Exists(path), path);

        Assert.DoesNotContain("SIDERA_", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheApplication_ReadsOnlyTwoVariables_BothOfThemRuntimeConfiguration()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var names = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "SIDERA_[A-Z0-9_]+").Select(m => m.Value))
            .Where(n => n != "SIDERA_")
            .Distinct()
            .Order()
            .ToList();

        // B: where the application keeps its files, for a second installation or a throw-away one. Everything else (SIDERA_*_OK, SIDERA_ASCOM_*, SIDERA_PHD2_*) belongs to the tests of real hardware.
        Assert.Equal(["SIDERA_EQUIPMENT_FILE", "SIDERA_SETTINGS_FILE"], names);
    }
}
