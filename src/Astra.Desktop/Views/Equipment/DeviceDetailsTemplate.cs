using Astra.Desktop.ViewModels;
using Astra.Desktop.Views.Equipment.DeviceDetails;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Astra.Desktop.Views.Equipment;

/// <summary>
/// Chooses the detail view of a device by its kind. Each kind of device has a view of its own, because each will have
/// settings of its own; a generic form that fits every device would fit none of them.
/// </summary>
public sealed class DeviceDetailsTemplate : IDataTemplate
{
    public bool Match(object? data) => data is DeviceDetailViewModel;

    public Control? Build(object? param) => param switch
    {
        CameraDetailViewModel => new CameraDetailsView(),
        FocuserDetailViewModel => new FocuserDetailsView(),
        FilterWheelDetailViewModel => new FilterWheelDetailsView(),
        MountDetailViewModel => new MountDetailsView(),
        GuiderDetailViewModel => new GuiderDetailsView(),
        null => null,
        _ => new TextBlock { Text = $"No detail view for {param.GetType().Name}." },
    };
}
