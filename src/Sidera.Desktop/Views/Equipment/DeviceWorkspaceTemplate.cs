using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Views.Equipment.Workspaces;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Sidera.Desktop.Views.Equipment;

/// <summary>
/// Chooses the workspace view of a device by its kind. The same views serve a standalone device and the device of a rig: a kind of
/// device has one workspace, wherever it was reached from.
/// </summary>
public sealed class DeviceWorkspaceTemplate : IDataTemplate
{
    public bool Match(object? data) => data is DeviceDetailViewModel;

    public Control? Build(object? param) => param switch
    {
        CameraDetailViewModel => new CameraWorkspaceView(),
        FocuserDetailViewModel => new FocuserWorkspaceView(),
        FilterWheelDetailViewModel => new FilterWheelWorkspaceView(),
        MountDetailViewModel => new MountWorkspaceView(),
        GuiderDetailViewModel => new GuiderWorkspaceView(),
        null => null,
        _ => new TextBlock { Text = $"No workspace for {param.GetType().Name}." },
    };
}
