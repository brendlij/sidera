using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.ViewModels;

/// <summary>One imaging setup that can be the current one: what the switcher in the sidebar offers.</summary>
/// <param name="Id">The imaging path of the setup (<see cref="ImagingBindingId"/>): the camera it images with. It is the same before and after somebody makes an explicit setup for a camera.</param>
/// <param name="Setup">The setup itself.</param>
/// <param name="Detail">What it is made of in a few words: "ASI2600MM · 750 mm".</param>
public sealed record SetupOption(ImagingBindingId Id, Rig Setup, string Detail)
{
    public string Name => Setup.Name;
}

/// <summary>
/// The imaging setup the application works with now. Imaging, autofocus, framing, plate solving and the equipment view of a setup all use it, so none of those pages asks which setup (or camera) is
/// meant again. With one setup that can image there is nothing to choose and nothing is shown but its name; with several, the sidebar offers the choice. The choice is kept by the camera of the setup
/// (its imaging path), so it survives a setup that is made, renamed or made again for the same camera. It changes what the pages work with and nothing else: the mount and the guider of the setups stay
/// shared as they are, and the session lanes are edited separately (selecting a lane does not change this, and this does not change the lane that is shown).
/// </summary>
public sealed partial class ImagingSetupContext : ObservableObject
{
    private readonly ImagingSetupCatalog _catalog;
    private readonly DeviceRegistry? _devices;

    public ImagingSetupContext(ImagingSetupCatalog catalog, DeviceRegistry? devices = null)
    {
        _catalog = catalog;
        _devices = devices;
        Refresh();
    }

    /// <summary>The setups that can image now, by name.</summary>
    public ObservableCollection<SetupOption> Options { get; } = [];

    /// <summary>The setup that is current; <c>null</c> when none can image (no camera, or several cameras that no setup names).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(HasCurrent), nameof(Name), nameof(Detail), nameof(HasCamera))]
    public partial SetupOption? Selected { get; set; }

    /// <summary>The current setup.</summary>
    public Rig? Current => Selected?.Setup;

    public bool HasCurrent => Selected is not null;

    public bool HasCamera => Selected is not null;

    /// <summary>The name of the current setup; empty when there is none.</summary>
    public string Name => Selected?.Name ?? string.Empty;

    public string Detail => Selected?.Detail ?? string.Empty;

    /// <summary>There are several setups to choose between: the selector is shown, clearly.</summary>
    public bool HasSeveral => Options.Count >= 2;

    /// <summary>There is exactly one setup: nothing is chosen, its name is shown quietly.</summary>
    public bool HasOne => Options.Count == 1;

    /// <summary>
    /// Why there is no current setup, in a sentence that says what to do; empty when there is one. More than one camera and no setup that names them is not guessed between.
    /// </summary>
    public string NoSetupText => Selected is not null ? string.Empty : CamerasCount() >= 2
        ? "Multiple imaging paths are available. Create or choose an Imaging Setup."
        : "There is no camera. Add one on the Equipment page.";

    public bool HasNoSetupText => NoSetupText.Length > 0;

    /// <summary>Several cameras and no setup to tell them apart: the pages say that a setup has to be created, and offer to.</summary>
    public bool NeedsSetup => Selected is null && CamerasCount() >= 2;

    /// <summary>The current setup, or another one, was chosen or changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Chooses the setup of this camera path. Returns whether there is such a setup that can image.</summary>
    public bool Select(ImagingBindingId id)
    {
        var option = Options.FirstOrDefault(o => o.Id == id);
        if (option is null)
        {
            return false;
        }

        Selected = option;
        return true;
    }

    /// <summary>Chooses a setup by its setup id (what a draft step names).</summary>
    public bool Select(RigId id) => Options.FirstOrDefault(o => o.Setup.Id == id) is { } option && Select(option.Id);

    /// <summary>
    /// Reads the setups again (a camera was connected, a setup made, renamed or removed) and keeps the choice: the same camera is the same setup, however its setup object changed. When it is gone the first
    /// setup is current; when nothing can image there is none.
    /// </summary>
    public void Refresh()
    {
        var previousSetup = Selected?.Setup.Id;
        var previous = Selected?.Id;
        var options = _catalog.UsableSetups().Select(rig => new SetupOption(ImagingBindingId.Of(rig), rig, Describe(rig))).ToList();
        var changed = !options.SequenceEqual(Options);
        if (changed)
        {
            Options.Clear();
            foreach (var option in options)
            {
                Options.Add(option);
            }
        }

        var next = options.FirstOrDefault(o => o.Setup.Id == previousSetup) ?? options.FirstOrDefault(o => o.Id == previous) ?? options.FirstOrDefault();
        var selectionChanged = !Equals(next, Selected);
        if (selectionChanged)
        {
            Selected = next; // raises Changed through OnSelectedChanged
        }

        if (changed || selectionChanged)
        {
            OnPropertyChanged(nameof(HasSeveral));
            OnPropertyChanged(nameof(HasOne));
            OnPropertyChanged(nameof(NoSetupText));
            OnPropertyChanged(nameof(HasNoSetupText));
            OnPropertyChanged(nameof(NeedsSetup));
            if (!selectionChanged)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    partial void OnSelectedChanged(SetupOption? value)
    {
        OnPropertyChanged(nameof(NoSetupText));
        OnPropertyChanged(nameof(HasNoSetupText));
        OnPropertyChanged(nameof(NeedsSetup));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string Describe(Rig rig)
    {
        var camera = _devices is not null && _devices.TryGet(rig.CameraId, out var device) ? device!.Name : rig.CameraId.Value;
        var optics = rig.Optics is { } o ? $" · {o.FocalLengthMm:0.#} mm" : string.Empty;
        return camera + optics;
    }

    private int CamerasCount() => _devices?.GetAll().OfType<ICamera>().Count() ?? 0;
}
