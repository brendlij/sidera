using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests;

/// <summary>The imaging setup of the application for the view models that are made without the shell.</summary>
internal static class TestSetups
{
    public static ImagingSetupContext ContextFor(SideraRuntimeHost host) => new(new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry), host.DeviceRegistry);

    /// <summary>Makes the setup of this name the current one.</summary>
    public static void Choose(this ImagingSetupContext context, string name) => context.Selected = context.Options.Single(o => o.Name == name);
}
