using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidera-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private LoggingOptions Options(string? folder = null, int retentionDays = 14, long maxFileBytes = LoggingOptions.DefaultMaxFileBytes) =>
        new(LogLevel.Trace, folder ?? Path.Combine(_root, "logs"), retentionDays, maxFileBytes);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static (ILoggerFactory Factory, RollingFileLoggerProvider Provider) Create(LoggingOptions options, TimeProvider? clock = null)
    {
        var provider = new RollingFileLoggerProvider(options, clock);
        return (LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider)), provider);
    }

    [Fact]
    public void ProviderCreatesTheDirectoryAndTheFile_BeforeTheFirstEntry()
    {
        var options = Options(Path.Combine(_root, "deep", "er", "logs"));

        using var provider = new RollingFileLoggerProvider(options);

        Assert.True(Directory.Exists(options.LogDirectory));
        Assert.True(File.Exists(provider.CurrentFilePath));
        Assert.Equal(options.LogDirectory, Path.GetDirectoryName(provider.CurrentFilePath));
    }

    [Fact]
    public void TheFileIsNamedAfterTheStartOfTheSession()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 22, 15, 30, TimeSpan.Zero));

        using var provider = new RollingFileLoggerProvider(Options(), clock);

        Assert.Equal("sidera-2026-10-03-221530.log", Path.GetFileName(provider.CurrentFilePath));
    }

    [Fact]
    public void AnEntryIsWrittenWithTimeLevelCategoryMessageAndScopes()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        var logger = factory.CreateLogger("Sidera.Runtime.Sequencing.SequenceRunner");

        using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("SessionId", "ab12cd34"), new("RigId", "rig.main") }))
        {
            logger.LogInformation("Device {DeviceId} connected", "camera.main");
        }

        factory.Dispose();

        provider.Dispose();

        var line = Assert.Single(File.ReadAllLines(path));
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d INF SequenceRunner: Device camera\.main connected \| ", line);
        Assert.EndsWith("| SessionId=ab12cd34 RigId=rig.main", line);
    }

    [Fact]
    public void TheSessionIdComesFirst_WhateverOrderTheScopesWereBegunIn()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        var logger = factory.CreateLogger("X");

        using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("RigId", "r"), new("SessionId", "s1") }))
        {
            logger.LogInformation("hello");
        }

        factory.Dispose();

        provider.Dispose();

        Assert.EndsWith("| SessionId=s1 RigId=r", File.ReadAllText(path).TrimEnd());
    }

    [Fact]
    public void AScopeBegunTwice_AppearsOnce()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        var logger = factory.CreateLogger("X");

        using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("DeviceId", "camera.main") }))
        using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("DeviceId", "camera.main"), new("RigId", "r") }))
        {
            logger.LogInformation("hello");
        }

        factory.Dispose();
        provider.Dispose();

        Assert.EndsWith("| DeviceId=camera.main RigId=r", File.ReadAllText(path).TrimEnd());
    }

    [Fact]
    public void AnExceptionIsWrittenWithItsTypeStackTraceAndInnerException()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        var logger = factory.CreateLogger("X");
        Exception caught;
        try
        {
            try
            {
                throw new InvalidOperationException("inner problem");
            }
            catch (Exception inner)
            {
                throw new ArgumentException("outer problem", inner);
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        logger.LogError(caught, "Autofocus failed for rig {RigId}", "rig.main");
        factory.Dispose();
        provider.Dispose();

        var text = File.ReadAllText(path);
        Assert.Contains("ERR X: Autofocus failed for rig rig.main", text);
        Assert.Contains("System.ArgumentException: outer problem", text);
        Assert.Contains("System.InvalidOperationException: inner problem", text);
        Assert.Contains("   at ", text); // a stack trace
    }

    [Fact]
    public void AMultiLineMessageStaysOneEntry_ContinuationLinesAreIndented()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;

        factory.CreateLogger("X").LogWarning("first\nsecond");
        factory.Dispose();
        provider.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("    second", lines[1]);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "CRT")]
    public void EveryLevelHasItsOwnShortName(LogLevel level, string text)
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;

        factory.CreateLogger("X").Log(level, "m");
        factory.Dispose();
        provider.Dispose();

        Assert.Contains($" {text} X: m", File.ReadAllText(path));
    }

    [Fact]
    public void DisposingWritesEverythingThatWasQueued()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        var logger = factory.CreateLogger("X");

        for (var i = 0; i < 2000; i++)
        {
            logger.LogInformation("entry {Number}", i);
        }

        factory.Dispose();

        provider.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(2000, lines.Length);
        Assert.EndsWith("entry 1999", lines[^1]);
    }

    [Fact]
    public async Task ConcurrentWritersNeitherLoseNorMixEntries()
    {
        var (factory, provider) = Create(Options());
        var path = provider.CurrentFilePath;
        const int writers = 8, perWriter = 1500;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            var logger = factory.CreateLogger("Writer" + writer);
            for (var i = 0; i < perWriter; i++)
            {
                using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("Writer", writer) }))
                {
                    logger.LogInformation("writer {Writer} entry {Number} of {Total} end", writer, i, perWriter);
                }
            }
        })));
        factory.Dispose();
        provider.Dispose();

        var lines = File.ReadAllLines(path);
        Assert.Equal(writers * perWriter, lines.Length);
        Assert.Equal(0, provider.DroppedCount);

        // Every line is whole: its writer, its number and its context agree.
        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"Writer(\d): writer (\d) entry (\d+) of 1500 end \| Writer=(\d)$");
            Assert.True(match.Success, line);
            Assert.Equal(match.Groups[1].Value, match.Groups[2].Value);
            Assert.Equal(match.Groups[1].Value, match.Groups[4].Value);
        }

        // Within one writer the entries are in order.
        for (var writer = 0; writer < writers; writer++)
        {
            var numbers = lines.Where(l => l.Contains($"Writer{writer}:"))
                .Select(l => int.Parse(System.Text.RegularExpressions.Regex.Match(l, @"entry (\d+) of").Groups[1].Value))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, perWriter), numbers);
        }
    }

    [Fact]
    public void AFileThatGetsTooBig_IsContinuedInANewOne_NothingIsLost()
    {
        var (factory, provider) = Create(Options(maxFileBytes: 4096));
        var first = provider.CurrentFilePath;
        var logger = factory.CreateLogger("X");

        for (var i = 0; i < 300; i++)
        {
            logger.LogInformation("a line of some length so that files fill up quickly {Number}", i);
        }

        var last = provider.CurrentFilePath;
        factory.Dispose();
        provider.Dispose();

        var files = Directory.GetFiles(Path.GetDirectoryName(first)!, "sidera-*.log").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.True(files.Length > 2, $"{files.Length} files");
        Assert.Contains(first, files);
        Assert.Matches(@"-\d+\.log$", files.Single(f => f != first && f.StartsWith(first[..^4], StringComparison.Ordinal) && f.EndsWith("-2.log")));
        var all = files.SelectMany(File.ReadAllLines).ToList();
        Assert.Equal(300, all.Count);
        Assert.Equal(Enumerable.Range(0, 300), all.Select(l => int.Parse(l[(l.LastIndexOf(' ') + 1)..])).Order());
    }

    [Fact]
    public void FilesOlderThanTheRetentionAreDeleted_NewerOnesAndOtherFilesStay()
    {
        var options = Options(retentionDays: 7);
        Directory.CreateDirectory(options.LogDirectory);
        var old = Path.Combine(options.LogDirectory, "sidera-2026-01-01-000000.log");
        var recent = Path.Combine(options.LogDirectory, "sidera-2026-09-30-000000.log");
        var foreign = Path.Combine(options.LogDirectory, "notes.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-30));

        using var provider = new RollingFileLoggerProvider(options);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(foreign)); // only Sidera's own log files are touched
        Assert.True(File.Exists(provider.CurrentFilePath));
    }

    [Fact]
    public void TwoSessionsThatStartInTheSameSecond_DoNotShareAFile()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 3, 22, 15, 30, TimeSpan.Zero));
        var (firstFactory, first) = Create(Options(), clock);
        firstFactory.CreateLogger("X").LogInformation("from the first");
        // Written once the file has content: the second one has to choose another name.
        firstFactory.Dispose();
        first.Dispose();

        var (secondFactory, second) = Create(Options(), clock);
        secondFactory.CreateLogger("X").LogInformation("from the second");
        secondFactory.Dispose();
        second.Dispose();

        Assert.NotEqual(first.CurrentFilePath, second.CurrentFilePath);
        Assert.Contains("from the first", File.ReadAllText(first.CurrentFilePath));
        Assert.DoesNotContain("from the second", File.ReadAllText(first.CurrentFilePath));
        Assert.Contains("from the second", File.ReadAllText(second.CurrentFilePath));
    }

    [Fact]
    public void AfterDisposal_LoggingIsHarmless()
    {
        var (factory, provider) = Create(Options());
        var logger = factory.CreateLogger("X");
        factory.Dispose();
        provider.Dispose();

        logger.LogInformation("too late");
        provider.Dispose(); // twice

        Assert.DoesNotContain("too late", File.ReadAllText(provider.CurrentFilePath));
    }

    [Fact]
    public void TheOptionsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new LoggingOptions(LogLevel.None, _root).Validate());
        Assert.Throws<ArgumentException>(() => new LoggingOptions(LogLevel.Debug, " ").Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggingOptions(LogLevel.Debug, _root, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoggingOptions(LogLevel.Debug, _root, 7, 10).Validate());
    }

    [Fact]
    public void TheDefaultDirectoryIsInTheUsersApplicationData_NotInTheWorkingOrProgramFolder()
    {
        var directory = LogLocation.DefaultDirectory();

        Assert.EndsWith(Path.Combine("Sidera", "logs"), directory);
        Assert.True(Path.IsPathRooted(directory));
        Assert.False(directory.StartsWith(Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase));
        Assert.False(directory.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directory);
        }
    }

    [Fact]
    public void ABuildHasAnExplicitDefaultLevel()
    {
        Assert.Equal(LogLevel.Debug, LoggingOptions.ForBuild(debugBuild: true).MinimumLevel);
        Assert.Equal(LogLevel.Information, LoggingOptions.ForBuild(debugBuild: false).MinimumLevel);
        Assert.Equal(LoggingOptions.DefaultRetentionDays, LoggingOptions.ForBuild(true).RetentionDays);
    }
}
