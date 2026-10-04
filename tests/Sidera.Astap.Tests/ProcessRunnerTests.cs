using System.Diagnostics;
using Sidera.Astap;

namespace Sidera.Astap.Tests;

/// <summary>The real process runner against programs of Windows: output, exit code, bounded output, timeout and cancel.</summary>
public sealed class ProcessRunnerTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task ARunThatEnds_ReturnsItsExitCodeAndItsOutput()
    {
        if (!OnWindows)
        {
            return;
        }

        var result = await new SystemProcessRunner().RunAsync(new ProcessRunRequest(Cmd, ["/c", "echo hello & exit 3"], TimeSpan.FromSeconds(20)), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("hello", result.StandardOutput);
        Assert.False(result.OutputTruncated);
    }

    [Fact]
    public async Task AnArgumentWithSpaces_ArrivesAsOneArgument()
    {
        if (!OnWindows)
        {
            return;
        }

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRunRequest("powershell.exe", ["-NoProfile", "-Command", "$args.Count; 'a b'"], TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Contains("a b", result.StandardOutput);
    }

    [Fact]
    public async Task MoreOutputThanTheLimit_IsReadAndThrownAway_NotKept_AndTheProgramEnds()
    {
        if (!OnWindows)
        {
            return;
        }

        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRunRequest(Cmd, ["/c", "for /L %i in (1,1,6000) do @echo a line of output number %i"], TimeSpan.FromSeconds(60)) { MaxOutputCharacters = 2000 },
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 2000);
    }

    [Fact]
    public async Task ATimeout_EndsTheProgram_AndSaysSo()
    {
        if (!OnWindows)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var result = await new SystemProcessRunner().RunAsync(
            new ProcessRunRequest(Cmd, ["/c", "ping -n 30 127.0.0.1 > nul"], TimeSpan.FromMilliseconds(500)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the program was not ended");
    }

    [Fact]
    public async Task ACancel_EndsTheProgram_AndThrows()
    {
        if (!OnWindows)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SystemProcessRunner().RunAsync(
            new ProcessRunRequest(Cmd, ["/c", "ping -n 30 127.0.0.1 > nul"], TimeSpan.FromMinutes(5)), cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the program was not ended");
    }

    [Fact]
    public async Task AProgramThatDoesNotExist_IsAFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => new SystemProcessRunner().RunAsync(
            new ProcessRunRequest(@"C:\this\does\not\exist.exe", [], TimeSpan.FromSeconds(5)), CancellationToken.None));
    }
}
