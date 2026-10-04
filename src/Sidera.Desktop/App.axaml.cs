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
            var equipment = new EquipmentService(host, store, factories, host.LoggerFactory.CreateLogger<EquipmentService>());
            equipment.Load();
            host.Start();
            var management = new EquipmentManagement(
                equipment,
                new AscomDiscovery(host.LoggerFactory.CreateLogger<AscomDiscovery>()),
                new AscomSetupService(drivers, host.LoggerFactory.CreateLogger<AscomSetupService>()));

            var filePicker = new AvaloniaSequenceFilePicker();
            var clipboard = new AvaloniaClipboardService();
            var viewModel = new MainViewModel(
                host, action => Dispatcher.UIThread.Post(action), filePicker: filePicker, logInfo: logInfo, clipboard: clipboard,
                equipmentManagement: management);

            var window = new MainWindow { DataContext = viewModel };
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

                try
                {
                    await host.StopAsync();
                }
                catch (Exception)
                {
                    // Nothing more to do at exit; every device was still tried, and the host logged each failure.
                }

                await host.DisposeAsync();
                logger.LogInformation("Sidera shut down cleanly");

                // Last: flushes and closes the log file.
                logging.Dispose();

                _shutdownComplete = true;
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
