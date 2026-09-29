using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Astra.Core.Devices;
using Astra.Desktop.ViewModels;
using Astra.Desktop.Views;
using Astra.Runtime;

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
            var host = new AstraRuntimeHost();
            var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
            host.Start();

            var viewModel = new MainViewModel(
                camera,
                host.EventBus,
                host.StateStore,
                action => Dispatcher.UIThread.Post(action)
            );

            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // Stop the runtime asynchronously without blocking the UI thread, then shut down for real.
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (_shutdownComplete)
                {
                    return;
                }

                e.Cancel = true;
                viewModel.Dispose();

                try
                {
                    await host.StopAsync();
                }
                catch (Exception)
                {
                    // Nothing more to do at exit; every device was still tried.
                }

                await host.DisposeAsync();

                _shutdownComplete = true;
                desktop.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
