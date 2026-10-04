using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop.Tests;

/// <summary>The logging composition of the application, and the diagnostics page.</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sidera-diag-tests-" + Guid.NewGuid().ToString("N"));

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

    private LoggingOptions Options(LogLevel level = LogLevel.Debug) => new(level, Path.Combine(_root, "logs"));

    private sealed class RecordingOpener : IFolderOpener
    {
        public List<string> Opened { get; } = [];
        public Exception? Failure { get; init; }

        public void Open(string folder)
        {
            Opened.Add(folder);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    // The composition

    [Fact]
    public async Task TheComposition_WritesTheRuntimesEntriesToAFileInTheLogFolder_AndFlushesOnDisposal()
    {
        var logging = SideraLogging.Create(Options(), console: false);
        await using (var host = new SideraRuntimeHost(loggerFactory: logging.Factory))
        {
            host.AddSimulatedCamera(new Sidera.Core.Devices.DeviceId("camera.main"), "Main Camera");
            host.Start();
            await host.DeviceOperations.ConnectAsync(new Sidera.Core.Devices.DeviceId("camera.main"));
            var path = logging.CurrentFilePath;
            var sessionId = host.SessionId;

            await host.StopAsync();
            logging.Dispose();

            Assert.StartsWith(Options().LogDirectory, path);
            var text = File.ReadAllText(path);
            Assert.Contains("Camera camera.main (Main Camera) connected", text);
            Assert.Contains("Runtime stopped", text);
            Assert.Contains($"SessionId={sessionId}", text);
            Assert.Contains("DeviceId=camera.main", text);
        }
    }

    [Fact]
    public async Task TheMinimumLevel_DecidesWhatIsWritten()
    {
        var logging = SideraLogging.Create(Options(LogLevel.Information), console: false);
        await using var host = new SideraRuntimeHost(loggerFactory: logging.Factory);
        host.AddSimulatedCamera(new Sidera.Core.Devices.DeviceId("camera.main"), "Main Camera");
        await host.DeviceOperations.ConnectAsync(new Sidera.Core.Devices.DeviceId("camera.main"));
        await host.DeviceOperations.ExposeAsync(new Sidera.Core.Devices.DeviceId("camera.main"), TimeSpan.FromMilliseconds(10));
        var path = logging.CurrentFilePath;

        logging.Dispose();

        var text = File.ReadAllText(path);
        Assert.Contains(" INF ", text);
        Assert.DoesNotContain(" DBG ", text); // the request, grant and release of the resource, the completion
        Assert.DoesNotContain(" TRC ", text);
    }

    [Fact]
    public void StartupLogging_NamesTheVersionPlatformRuntimeAndLog_AndNothingPersonal()
    {
        var logging = SideraLogging.Create(Options(), console: false);
        var path = logging.CurrentFilePath;

        SideraLogging.LogStartup(logging.Factory.CreateLogger("Startup"), logging.Info("abc12345"));
        logging.Dispose();

        var text = File.ReadAllText(path);
        Assert.Contains("Sidera ", text);
        Assert.Contains(".NET", text);
        Assert.Contains(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), text);
        Assert.Contains("Logging to " + path, text);
        var withoutPath = text.Replace(path, string.Empty);
        foreach (var personal in new[] { Environment.UserName, Environment.MachineName }.Where(name => name.Length >= 5))
        {
            Assert.DoesNotContain(personal, withoutPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheOptionsAreCheckedBeforeAnythingIsCreated()
    {
        Assert.Throws<ArgumentException>(() => SideraLogging.Create(new LoggingOptions(LogLevel.None, _root)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void TheBuildDecidesTheDefaultLevel_Explicitly()
    {
        Assert.Equal(SideraLogging.IsDebugBuild ? LogLevel.Debug : LogLevel.Information, LoggingOptions.ForBuild(SideraLogging.IsDebugBuild).MinimumLevel);
    }

    // The diagnostics page

    [Fact]
    public void ThePage_ShowsTheFolderTheFileTheLevelAndTheSession()
    {
        var info = new LogInfo("C:\\logs", "C:\\logs\\sidera-2026-10-03-221530.log", LogLevel.Debug, "ab12cd34");

        var vm = new DiagnosticsViewModel(info, new RecordingOpener());

        Assert.True(vm.IsLoggingConfigured);
        Assert.Equal("C:\\logs\\sidera-2026-10-03-221530.log", vm.LogFileText);
        Assert.Equal("C:\\logs", vm.LogFolderText);
        Assert.Equal("Debug", vm.LevelText);
        Assert.Equal("ab12cd34", vm.SessionText);
        Assert.False(string.IsNullOrWhiteSpace(vm.VersionText));
    }

    [Fact]
    public void OpeningTheFolder_HandsTheFolderToTheOpener()
    {
        var opener = new RecordingOpener();
        var vm = new DiagnosticsViewModel(new LogInfo("C:\\logs", "C:\\logs\\a.log", LogLevel.Information, "s"), opener);

        vm.OpenLogFolderCommand.Execute(null);

        Assert.Equal(["C:\\logs"], opener.Opened);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void IfTheFolderCannotBeOpened_ThePathIsShownInTheError()
    {
        var opener = new RecordingOpener { Failure = new InvalidOperationException("no file manager") };
        var vm = new DiagnosticsViewModel(new LogInfo("C:\\logs", "C:\\logs\\a.log", LogLevel.Information, "s"), opener);

        vm.OpenLogFolderCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Contains("C:\\logs", vm.ErrorMessage);
        Assert.Same(opener.Failure, vm.LastException);
    }

    [Fact]
    public void WithoutLogging_ThePageSaysSo_AndTheFolderCannotBeOpened()
    {
        var opener = new RecordingOpener();
        var vm = new DiagnosticsViewModel(null, opener);

        Assert.False(vm.IsLoggingConfigured);
        Assert.Equal("Logging is not configured.", vm.LogFileText);
        Assert.False(vm.OpenLogFolderCommand.CanExecute(null));
        Assert.Empty(opener.Opened);
    }

    [Fact]
    public async Task TheDiagnosticsPage_IsReachableFromTheMainViewModel()
    {
        await using var host = new SideraRuntimeHost();
        using var vm = new MainViewModel(host, action => action(), logInfo: new LogInfo("C:\\logs", "C:\\logs\\a.log", LogLevel.Debug, host.SessionId));

        vm.NavigateCommand.Execute(AppPage.Diagnostics);

        Assert.Same(vm.Diagnostics, vm.CurrentPage);
        Assert.True(vm.SecondaryNavigation.Single(item => item.Page == AppPage.Diagnostics).IsSelected);
        Assert.DoesNotContain(vm.PrimaryNavigation, item => item.IsSelected);
        Assert.Equal(host.SessionId, vm.Diagnostics.SessionText);
    }
}
