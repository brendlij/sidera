using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Astra.Ascom;
using Astra.Ascom.Discovery;
using Astra.Ascom.Drivers;
using Astra.Desktop.Diagnostics;
using Astra.Desktop.Hardware;
using Astra.Desktop.ViewModels;
using Astra.Desktop.Views;
using Astra.Desktop.Views.Shell;
using Astra.Runtime;
using Astra.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Astra.Desktop;

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
            var logging = AstraLogging.Create(LoggingOptions.ForBuild(AstraLogging.IsDebugBuild));
            var host = new AstraRuntimeHost(loggerFactory: logging.Factory);
            var logInfo = logging.Info(host.SessionId);
            var logger = host.LoggerFactory.CreateLogger<App>();
            AstraLogging.LogStartup(logger, logInfo);
            ReportUnhandledExceptions(logger);

            // The equipment is what the user configured: loaded from the equipment file, every device disconnected. A first
            // start has none; the equipment page offers to add devices (ASCOM or simulated) or the simulated demo.
            var drivers = new ComAscomDriverFactory();
            var factories = new DeviceFactoryRegistry(
                [new SimulatorDeviceFactory(), new AscomBackendFactory(new AscomDeviceFactory(drivers, host.LoggerFactory))]);
            var equipmentFile = Environment.GetEnvironmentVariable("ASTRA_EQUIPMENT_FILE");
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
                logger.LogInformation("Astra is shutting down");
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
                logger.LogInformation("Astra shut down cleanly");

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
