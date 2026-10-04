using System.Globalization;
using Sidera.Astap;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;

namespace Sidera.Astap.Tests;

/// <summary>An installation on paper: the files and folders it says exist.</summary>
internal sealed class FakeFileSystem : IAstapFileSystem
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Roots { get; } = [@"C:\Program Files"];

    public IReadOnlyList<string> InstallRoots => Roots;

    public bool FileExists(string path) => Files.Contains(path);

    public bool DirectoryExists(string path) => Folders.ContainsKey(path);

    public IReadOnlyList<string> FileNames(string directory) => Folders.TryGetValue(directory, out var names) ? names : [];

    /// <summary>ASTAP in Program Files with the given database tiles in its folder.</summary>
    public static FakeFileSystem Installed(string? databaseName = "d50", int tiles = 3, bool console = true)
    {
        var fs = new FakeFileSystem();
        var folder = @"C:\Program Files\astap";
        fs.Files.Add(Path.Combine(folder, "astap.exe"));
        if (console)
        {
            fs.Files.Add(Path.Combine(folder, "astap_cli.exe"));
        }

        var names = new List<string> { "astap.exe", "deep_sky.csv" };
        if (databaseName is not null)
        {
            for (var i = 1; i <= tiles; i++)
            {
                names.Add($"{databaseName}_{i:0000}.1476");
            }
        }

        fs.Folders[folder] = names;
        return fs;
    }
}

/// <summary>A process runner that pretends to be ASTAP: it writes the .ini that a script says and returns the exit code.</summary>
internal sealed class FakeRunner(Func<int, ProcessRunRequest, (int ExitCode, string? Ini, bool TimedOut)> script) : IProcessRunner
{
    public List<ProcessRunRequest> Runs { get; } = [];

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Runs.Add(request);
        var (exit, ini, timedOut) = script(Runs.Count, request);
        if (ini is not null)
        {
            var output = request.Arguments[request.Arguments.ToList().IndexOf("-o") + 1];
            File.WriteAllText(output + ".ini", ini);
        }

        return Task.FromResult(new ProcessRunResult(exit, timedOut, string.Empty, string.Empty, false, TimeSpan.FromMilliseconds(5)));
    }
}

internal static class AstapSamples
{
    /// <summary>The .ini of a solution at the given place, with the matrix of the rotation, in the way ASTAP writes numbers (an exponent, a dot).</summary>
    public static string SolvedIni(double raDegrees, double decDegrees, double scaleArcsec, double rotationDegrees, bool mirrored = false, bool withCd = true)
    {
        var cdelt = scaleArcsec / 3600.0;
        var r = rotationDegrees * Math.PI / 180.0;
        var cdelt1 = mirrored ? cdelt : -cdelt;
        var lines = new List<string>
        {
            "PLTSOLVD=T",
            "CRPIX1=" + E(3124.5),
            "CRPIX2=" + E(2088.5),
            "CRVAL1=" + E(raDegrees),
            "CRVAL2=" + E(decDegrees),
            "CDELT1=" + E(cdelt1),
            "CDELT2=" + E(cdelt),
            "CROTA1=" + E(rotationDegrees),
            "CROTA2=" + E(rotationDegrees),
        };
        if (withCd)
        {
            lines.Add("CD1_1=" + E(cdelt1 * Math.Cos(r)));
            lines.Add("CD1_2=" + E(-cdelt * Math.Sin(r)));
            lines.Add("CD2_1=" + E(cdelt1 * Math.Sin(r)));
            lines.Add("CD2_2=" + E(cdelt * Math.Cos(r)));
        }

        lines.Add("DATE=2026-10-04");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string E(double value) => value.ToString("E12", CultureInfo.InvariantCulture).PadLeft(22);

    public const string Failed = "PLTSOLVD=F\r\nERROR=No solution found\r\n";

    public static PlateSolveImage Image(int width = 600, int height = 400) =>
        new(new CameraFrame(width, height, new ushort[width * height], TimeSpan.FromSeconds(2)));
}
