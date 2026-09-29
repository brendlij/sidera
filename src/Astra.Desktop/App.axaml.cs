using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Astra.Core.Devices;
using Astra.Desktop.ViewModels;
using Astra.Desktop.Views;
using Astra.Runtime.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.State;

namespace Astra.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // StateStore must subscribe before the view model so it is updated first.
            var eventBus = new EventBus();
            var stateStore = new StateStore(eventBus);
            var registry = new DeviceRegistry();
            var camera = new SimulatedCamera(new DeviceId("camera.main"), "Main Camera", eventBus);
            registry.Register(camera);

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(
                    camera,
                    eventBus,
                    stateStore,
                    action => Dispatcher.UIThread.Post(action)
                ),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
