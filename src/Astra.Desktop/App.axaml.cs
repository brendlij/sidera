using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            DemoSetup.AddDemoEquipment(host);
            DemoSetup.AddDemoRigs(host);
            host.Start();

            var filePicker = new AvaloniaSequenceFilePicker();
            var viewModel = new MainViewModel(host, action => Dispatcher.UIThread.Post(action), filePicker: filePicker);

            var window = new MainWindow { DataContext = viewModel };
            filePicker.Attach(window);
            desktop.MainWindow = window;

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
