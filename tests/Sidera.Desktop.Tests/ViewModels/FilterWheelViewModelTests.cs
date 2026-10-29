using Sidera.Ascom;
using Sidera.Ascom.FilterWheels;
using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The filter wheel card of a wheel that is not connected: loading the equipment must not need a connection.</summary>
public sealed class FilterWheelViewModelTests
{
    [Fact]
    public async Task AnAscomWheelThatIsNotConnected_HasACard_AndDoesNotStopTheApplication()
    {
        await using var host = new SideraRuntimeHost();
        var wheel = new AscomFilterWheel(new DeviceId("filterwheel.main"), "ZWO FilterWheel (1)", "ASCOM.Test.FilterWheel", null!);

        using var vm = new FilterWheelViewModel(wheel, host, a => a(), new SessionActivity());

        Assert.Equal("—", vm.CurrentFilterText);
        Assert.Null(vm.SelectedFilter);
        Assert.False(vm.ChangeCommand.CanExecute(null));
    }
}
