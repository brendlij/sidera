using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Ascom.Drivers;
using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Views;
using Sidera.Desktop.Views.Shell;
using Sidera.Core;
using Sidera.Runtime;
using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop;

public partial class App : Application
{
    private bool _shutdownComplete;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The one place where logging is configured; it exists before the runtime and the view models do.
            var logging = SideraLogging.Create(LoggingOptions.ForBuild(SideraLogging.IsDebugBuild));
            var host = new SideraRuntimeHost(loggerFactory: logging.Factory);
            var logInfo = logging.Info(host.SessionId);
            var logger = host.LoggerFactory.CreateLogger<App>();
            SideraLogging.LogStartup(logger, logInfo);
            ReportUnhandledExceptions(logger);

            // The equipment is what the user configured: loaded from the equipment file, every device disconnected. A first
            // start has none; the equipment page offers to add devices (ASCOM or simulated) or the simulated demo.
            var drivers = new ComAscomDriverFactory();
            var factories = new DeviceFactoryRegistry(
                [new SimulatorDeviceFactory(), new AscomBackendFactory(new AscomDeviceFactory(drivers, host.LoggerFactory)), new Phd2BackendFactory(host.LoggerFactory)]);
            var equipmentFile = SideraEnvironment.Get("SIDERA_EQUIPMENT_FILE");
            if (string.IsNullOrWhiteSpace(equipmentFile))
            {
                // The equipment of the time the product was called Astra comes along on the first start, once and without touching it.
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var migration = LegacyAstraData.MigrateEquipmentFile(LegacyAstraData.EquipmentFile(appData), LegacyAstraData.LegacyEquipmentFile(appData));
                if (migration.Outcome == LegacyMigrationOutcome.Copied)
                {
                    logger.LogInformation("The equipment file was copied from {Legacy} to {Current}; the old one is left as it is",
                        LegacyAstraData.LegacyEquipmentFile(appData), LegacyAstraData.EquipmentFile(appData));
                }
                else if (migration.Outcome == LegacyMigrationOutcome.Failed)
                {
                    logger.LogWarning("The equipment file of {Legacy} could not be copied: {Problem}", LegacyAstraData.LegacyEquipmentFile(appData), migration.Problem);
                }
            }

            var store = string.IsNullOrWhiteSpace(equipmentFile)
                ? EquipmentConfigurationStore.CreateDefault()
                : new EquipmentConfigurationStore(equipmentFile);
            var settingsFile = SideraEnvironment.Get("SIDERA_SETTINGS_FILE");
            var site = new SiteService(string.IsNullOrWhiteSpace(settingsFile) ? SideraSettingsStore.CreateDefault() : new SideraSettingsStore(settingsFile));
            site.Load();
            host.ConfigurePlateSolver(new Sidera.Astap.AstapPlateSolver(() => new Sidera.Astap.AstapConfiguration
            {
                ExecutablePath = site.PlateSolving.ExecutablePath,
                DatabasePath = site.PlateSolving.DatabasePath,
            }, logger: host.LoggerFactory.CreateLogger<Sidera.Astap.AstapPlateSolver>()));
            if (site.Problem is { } siteProblem)
            {
                logger.LogWarning("The settings file could not be used: {Problem}", siteProblem);
            }

            var equipment = new EquipmentService(host, store, factories, host.LoggerFactory.CreateLogger<EquipmentService>());
            equipment.Load();
            host.Start();
            var management = new EquipmentManagement(
                equipment,
                new AscomDiscovery(host.LoggerFactory.CreateLogger<AscomDiscovery>()),
                new AscomSetupService(drivers, host.LoggerFactory.CreateLogger<AscomSetupService>()),
                site);

            var filePicker = new AvaloniaSequenceFilePicker();
            var clipboard = new AvaloniaClipboardService();
            // The sky atlas of the framing workspace: surveys over HTTP into a bounded cache on disk, and a catalog of objects that works without the network where it can.
            var atlas = site.SkyAtlas;
            var skyHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(atlas.NetworkTimeoutSeconds + 5) };
            skyHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Sidera");
            var skyCache = new Sidera.Sky.SkyTileCache(string.IsNullOrWhiteSpace(atlas.CacheDirectory) ? Sidera.Sky.SkyTileCache.DefaultRoot() : atlas.CacheDirectory, atlas.MaxCacheBytes);
            var skyDecoder = new Imaging.SkiaTileDecoder();
            var hipsOptions = new Sidera.Sky.HiPSOptions { RequestTimeout = TimeSpan.FromSeconds(atlas.NetworkTimeoutSeconds) };
            var astapFolder = Sidera.Astap.AstapLocator.Locate(new Sidera.Astap.AstapConfiguration { ExecutablePath = site.PlateSolving.ExecutablePath }, new Sidera.Astap.SystemAstapFileSystem()).ExecutablePath is { } astapExe
                ? System.IO.Path.GetDirectoryName(astapExe) : null;
            var objectCatalog = new Sidera.Sky.CompositeObjectCatalog(
                new Sidera.Sky.DeepSkyCsvCatalog(System.IO.Path.Combine(astapFolder ?? string.Empty, "deep_sky.csv")),
                new Sidera.Sky.SesameCatalog(skyHttp, TimeSpan.FromSeconds(atlas.NetworkTimeoutSeconds)));

            var viewModel = new MainViewModel(
                host, action => Dispatcher.UIThread.Post(action), filePicker: filePicker, logInfo: logInfo, clipboard: clipboard,
                equipmentManagement: management, withDemoSequence: false, objectCatalog: objectCatalog, startWithSettingsMode: true,
                skyProviders: survey => new Sidera.Sky.HiPSSurveyProvider(survey, skyHttp, skyCache, skyDecoder, hipsOptions, host.LoggerFactory.CreateLogger<Sidera.Sky.HiPSSurveyProvider>()));

            var window = new MainWindow { DataContext = viewModel };
            if (viewModel.Settings.PlateSolving is { } solverSettings)
                solverSettings.BrowseExecutable = async () =>
                {
                    var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                    { Title = "Select ASTAP executable", AllowMultiple = false });
                    return files.Count > 0 ? files[0].Path.LocalPath : null;
                };
            filePicker.Attach(window);
            clipboard.Attach(window);
            desktop.MainWindow = window;

            // Stop the runtime asynchronously without blocking the UI thread, then shut down for real.
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (_shutdownComplete)
                {
                    return;
                }

                e.Cancel = true;
                logger.LogInformation("Sidera is shutting down");
                viewModel.Dispose();

                // A device that never answers must not keep the window open: each step gets a bounded time, then the process goes on.
                var inTime = await WithinAsync(StopHostAsync(host), ShutdownStepTimeout);
                inTime &= await WithinAsync(host.DisposeAsync().AsTask(), ShutdownStepTimeout);
                if (!inTime)
                {
                    logger.LogWarning("Shutdown did not finish within {Seconds} s per step; leaving anyway", ShutdownStepTimeout.TotalSeconds);
                }

                logger.LogInformation("Sidera shut down cleanly");

                // Last: flushes and closes the log file.
                logging.Dispose();

                _shutdownComplete = true;
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static readonly TimeSpan ShutdownStepTimeout = TimeSpan.FromSeconds(8);

    private static async Task<bool> WithinAsync(Task work, TimeSpan limit) =>
        await Task.WhenAny(work, Task.Delay(limit)) == work;

    private static async Task StopHostAsync(SideraRuntimeHost host)
    {
        try
        {
            await host.StopAsync();
        }
        catch (Exception)
        {
            // Nothing more to do at exit; every device was still tried, and the host logged each failure.
        }
    }

    // What nobody handled goes into the log before the process goes (or the task is forgotten), with its stack trace.
    private static void ReportUnhandledExceptions(ILogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(
                e.ExceptionObject as Exception, "Unhandled exception; the application is terminating: {IsTerminating}", e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.LogError(e.Exception, "A task failed and nobody observed its exception");
    }
}
