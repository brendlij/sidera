using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>
/// The identity of an imaging path: derived from the camera and nothing else, the same for the implicit setup of a single camera and for an explicit setup made later for it, unchanged by renames, and
/// never carried to another camera.
/// </summary>
public sealed class ImagingBindingIdTests : IAsyncLifetime
{
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly DeviceId Main = new("camera.main");
    private static readonly DeviceId Wide = new("camera.wide");
    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private SideraRuntimeHost NewHost(string mainName = "ASI2600MM", bool wide = false)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedCamera(Main, mainName, 1);
        if (wide)
        {
            host.AddSimulatedCamera(Wide, "ASI533MC", 2);
        }

        return host;
    }

    private static ImagingSetupCatalog Catalog(SideraRuntimeHost host) => new(host.DeviceRegistry, host.RigRegistry);

    [Fact]
    public void ThePath_IsDerivedFromTheCameraAlone()
    {
        var path = ImagingBindingId.For(Main);

        Assert.Equal("imaging:auto:camera.main", path.Value);
        Assert.True(path.IsPath);
        Assert.True(path.TryGetCamera(out var camera));
        Assert.Equal(Main, camera);

        // Not from the id, the name or the optics of a setup.
        Assert.Equal(path, ImagingBindingId.Of(new Rig(new RigId("rig.a"), "One", Main, Optics)));
        Assert.Equal(path, ImagingBindingId.Of(new Rig(new RigId("rig.b"), "Another name", Main, optics: null)));
        Assert.NotEqual(path, ImagingBindingId.For(Wide));
    }

    [Fact]
    public void ASetupId_IsNotAPath_ButStillAReference()
    {
        ImagingBindingId reference = new RigId("rig.main");

        Assert.False(reference.IsPath);
        Assert.False(reference.TryGetCamera(out _));
        Assert.Equal("rig.main", reference.Value);
    }

    [Fact]
    public void TheImplicitSetup_HasAnIdDerivedFromItsCamera_AndItIsTheSameAfterARestart()
    {
        var first = Assert.Single(Catalog(NewHost()).GetAll());
        var restarted = Assert.Single(Catalog(NewHost(mainName: "A name the camera did not have before")).GetAll()); // a new host, the same camera id, another display name

        Assert.Equal(new RigId("setup.implicit:camera.main"), first.Id);
        Assert.Equal(ImagingSetupCatalog.ImplicitIdFor(Main), first.Id);
        Assert.Equal(first.Id, restarted.Id);
        Assert.Equal(ImagingBindingId.Of(first), ImagingBindingId.Of(restarted));
    }

    [Fact]
    public void AnExplicitSetup_IsTheSamePathAsTheImplicitOne_AndItsOpticsAreThereAtOnce()
    {
        var host = NewHost();
        var catalog = Catalog(host);
        var path = ImagingBindingId.For(Main);

        Assert.True(catalog.TryResolve(path, out var implicitSetup));
        Assert.Null(implicitSetup!.Optics);

        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics));

        Assert.True(catalog.TryResolve(path, out var explicitSetup));
        Assert.Equal(new RigId("rig.main"), explicitSetup!.Id);
        Assert.Equal(750, explicitSetup.Optics!.FocalLengthMm); // the optical metadata of the explicit setup, without anything else changing
        Assert.Equal(path, ImagingBindingId.Of(explicitSetup));
        Assert.Single(catalog.GetAll()); // the implicit one is not listed beside it
    }

    [Fact]
    public void WhatNamedTheImplicitSetupBefore_MeansTheExplicitSetupAfterwards()
    {
        var host = NewHost();
        var catalog = Catalog(host);
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics));

        // The id derived from the camera, the constant id that documents wrote before it was derived, and the id of the setup itself.
        foreach (ImagingBindingId reference in new[] { (ImagingBindingId)new RigId("setup.implicit:camera.main"), new RigId("setup.implicit"), new RigId("rig.main") })
        {
            Assert.True(catalog.TryResolve(reference, out var rig), reference.Value);
            Assert.Equal(new RigId("rig.main"), rig!.Id);
        }
    }

    [Fact]
    public void RenamingOrRecreatingASetup_KeepsThePath_AndAReferenceToTheOldSetupObjectNoLongerResolves()
    {
        var host = NewHost();
        var catalog = Catalog(host);
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics));
        var path = ImagingBindingId.For(Main);

        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.AddRig(new Rig(new RigId("rig.renamed"), "Another name", Main, Optics));

        Assert.True(catalog.TryResolve(path, out var rig));
        Assert.Equal(new RigId("rig.renamed"), rig!.Id);
        Assert.Equal("Another name", rig.Name);
        Assert.False(catalog.TryResolve(new RigId("rig.main"), out _)); // the old setup object is gone: it is not guessed into the new one
    }

    [Fact]
    public void SeveralCamerasAndNoSetup_StayAmbiguous_ForEveryKindOfReference()
    {
        var host = NewHost(wide: true);
        var catalog = Catalog(host);

        Assert.Empty(catalog.GetAll());
        Assert.False(catalog.TryResolve(ImagingBindingId.For(Main), out _));
        Assert.False(catalog.TryResolve(ImagingBindingId.For(Wide), out _));
        Assert.False(catalog.TryResolve(new RigId("setup.implicit"), out _));
        Assert.False(catalog.TryResolve(new RigId("setup.implicit:camera.main"), out _));
    }

    [Fact]
    public void AReplacedCamera_IsAnotherPath_AndTheOldPathIsNotCarriedOver()
    {
        var host = NewHost(wide: true);
        var catalog = Catalog(host);
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics));
        Assert.True(catalog.TryResolve(ImagingBindingId.For(Main), out _));

        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Wide, Optics)); // the same setup object, another camera

        Assert.False(catalog.TryResolve(ImagingBindingId.For(Main), out _)); // what was bound to the old camera does not become the new one
        Assert.True(catalog.TryResolve(ImagingBindingId.For(Wide), out var rig));
        Assert.Equal(new RigId("rig.main"), rig!.Id);
    }

    [Fact]
    public void ARemovedCamera_LeavesItsPathUnresolved()
    {
        var host = NewHost(wide: true);
        var catalog = Catalog(host);
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics));
        host.RigRegistry.Unregister(new RigId("rig.main"));
        Assert.True(host.RemoveDevice(Main));

        Assert.False(catalog.TryResolve(ImagingBindingId.For(Main), out _));
        Assert.True(catalog.TryResolve(ImagingBindingId.For(Wide), out var remaining)); // the one camera that is left is a setup of its own, and a different path
        Assert.Equal(ImagingSetupCatalog.ImplicitIdFor(Wide), remaining!.Id);
    }
}
