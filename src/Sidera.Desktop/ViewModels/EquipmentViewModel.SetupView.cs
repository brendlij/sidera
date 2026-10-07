using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The current imaging setup as the equipment page shows it first: what the setup is made of (camera, focuser, filter wheel, rotator), its optics, and the mount and the guider it shares with
/// other setups. Nothing of how devices are managed is here; that is the page behind "Manage all devices".
/// </summary>
public sealed class SetupSummaryViewModel
{
    private static readonly string[] OwnRoles = ["Camera", "Focuser", "Filter Wheel", "Rotator"];
    private static readonly string[] SharedRoles = ["Mount", "Guider"];

    public SetupSummaryViewModel(RigViewModel rig, bool isImplicit)
    {
        Rig = rig;
        IsImplicit = isImplicit;
        Own = [.. rig.Parts.Where(p => OwnRoles.Contains(p.Role, StringComparer.Ordinal))];
        Shared = [.. rig.Parts.Where(p => SharedRoles.Contains(p.Role, StringComparer.Ordinal))];
    }

    public RigViewModel Rig { get; }

    /// <summary>The setup follows from the one camera there is: nobody made it, and it has no optics until somebody does.</summary>
    public bool IsImplicit { get; }

    public string Name => Rig.Name;

    /// <summary>"MAIN 750MM": how the page names the setup.</summary>
    public string Title => Rig.Name.ToUpperInvariant();

    /// <summary>The camera, the focuser, the filter wheel and the rotator the setup has.</summary>
    public IReadOnlyList<RigPartViewModel> Own { get; }

    /// <summary>The mount and the guider; another setup may use the same ones.</summary>
    public IReadOnlyList<RigPartViewModel> Shared { get; }

    public bool HasShared => Shared.Count > 0;

    public bool HasOptics => Rig.Optics is not null;

    public string FocalLengthText => Rig.FocalLengthText;

    public string ApertureText => Rig.ApertureText;

    public string PixelScaleText => Rig.PixelScaleText;

    public string FieldOfViewText => Rig.FieldOfViewText;

    /// <summary>What is missing for the optics, in a sentence; empty when they are there.</summary>
    public string OpticsHint => HasOptics ? string.Empty : IsImplicit
        ? "This setup follows from your camera and has no optics. Create an Imaging Setup to give it a focal length: framing and plate solving use it."
        : "This setup has no optics yet. Set the focal length on the camera page: framing and plate solving use it.";

    public bool HasOpticsHint => OpticsHint.Length > 0;
}

public sealed partial class EquipmentViewModel
{
    private ImagingSetupContext? _setupContext;
    private bool _manageAll;
    private readonly Dictionary<Sidera.Core.Rigs.RigId, RigViewModel> _implicitViews = [];

    /// <summary>The view model of an imaging setup, made or implied by the one camera there is: what the dashboard and the setup view show of it.</summary>
    public RigViewModel ViewOf(Sidera.Core.Rigs.Rig rig)
    {
        if (_rigs.FirstOrDefault(r => r.Id == rig.Id) is { } explicitView)
        {
            return explicitView;
        }

        if (!_implicitViews.TryGetValue(rig.Id, out var view))
        {
            view = new RigViewModel(rig, _host, _cameras, _focusers, _filterWheels, _mounts, _guiders, _rotators);
            foreach (var part in view.Parts)
            {
                var device = part.Device;
                part.OpenCommand = new RelayCommand(() => OpenDevice(device));
            }

            _implicitViews[rig.Id] = view;
        }

        return view;
    }

    /// <summary>The current imaging setup; <c>null</c> when no setup can image (no camera, or several that no setup names), and then the page starts with all the devices.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupView), nameof(CanReturnToSetup), nameof(ReturnToSetupText), nameof(ShowSectionTabs))]
    public partial SetupSummaryViewModel? Summary { get; private set; }

    /// <summary>The page shows the current imaging setup, not the management of every device.</summary>
    public bool IsSetupView => Summary is not null && !_manageAll;

    /// <summary>The page shows every device, and there is a setup to go back to.</summary>
    public bool CanReturnToSetup => Summary is not null && _manageAll;

    public string ReturnToSetupText => Summary is null ? string.Empty : "‹ " + Summary.Name;

    /// <summary>The tabs of the page (by kind of device) are the management of every device: they are not there in the view of a setup.</summary>
    public bool ShowSectionTabs => ShowSections && !IsSetupView;

    /// <summary>The equipment page follows the imaging setup of the application. Called once by the shell.</summary>
    internal void AttachSetupContext(ImagingSetupContext context)
    {
        _setupContext = context;
        context.Changed += (_, _) => RebuildSummary();
        RebuildSummary();
    }

    // The summary of the current setup: from the setup view model of the equipment when it was made, else one made of the devices of the implied setup.
    private void RebuildSummary()
    {
        var rig = _setupContext?.Current;
        if (rig is null)
        {
            Summary = null;
            NotifySetupView();
            return;
        }

        Summary = new SetupSummaryViewModel(ViewOf(rig), !_rigs.Any(r => r.Id == rig.Id));
        NotifySetupView();
    }

    private void NotifySetupView()
    {
        OnPropertyChanged(nameof(IsSetupView));
        OnPropertyChanged(nameof(CanReturnToSetup));
        OnPropertyChanged(nameof(ReturnToSetupText));
        OnPropertyChanged(nameof(ShowSectionTabs));
    }

    /// <summary>Shows the current imaging setup (the page starts there when it is opened from the sidebar).</summary>
    [RelayCommand]
    public void ShowSetupView()
    {
        _manageAll = false;
        _contextKey = null;
        Apply();
        NotifySetupView();
    }

    /// <summary>Opens the management of every device: cameras, focusers, filter wheels, rotators, mounts and guiders, and the imaging setups.</summary>
    [RelayCommand]
    private void ManageAllDevices()
    {
        _manageAll = true;
        _contextKey = null;
        Apply();
        NotifySetupView();
    }

    /// <summary>Shows the devices of a kind (the focusers, for example), as if the user had opened the management and chosen it.</summary>
    public void ShowKind(EquipmentPage page)
    {
        _manageAll = true;
        OpenKind(page);
        NotifySetupView();
    }

    /// <summary>Shows where the current setup gets a focuser: its own page when it was made, else the focusers, where one is added.</summary>
    public void ShowFocuserSetup()
    {
        if (_setupContext?.Current is { } rig && _rigs.FirstOrDefault(r => r.Id == rig.Id) is { } explicitSetup)
        {
            _manageAll = true;
            OpenRig(explicitSetup);
            NotifySetupView();
            return;
        }

        ShowKind(EquipmentPage.Focuser);
    }

    /// <summary>Shows where an imaging setup is made, and starts one; the camera is chosen there.</summary>
    public void BeginNewImagingSetup()
    {
        _manageAll = true;
        _contextKey = null;
        Apply();
        NotifySetupView();
        if (AddRig is { } add && add.BeginCommand.CanExecute(null))
        {
            add.BeginCommand.Execute(null);
        }
    }
}
