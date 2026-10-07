using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;

namespace Sidera.Desktop.ViewModels;

/// <summary>A device that can be given to a rig for a role, or "None" (<see cref="Id"/> is <c>null</c>) for an optional one.</summary>
public sealed record AssignmentChoice(string? Id, string Text);

/// <summary>
/// One role of a rig and the device that plays it, as a choice. Choosing another device (or none, for what is optional) changes the rig at once and saves it; a device that another rig has as its own
/// camera, focuser, filter wheel or rotator is not offered, and a mount or guider that another rig has may be chosen too (the rigs then share it). The camera is never taken away.
/// </summary>
public sealed partial class RigAssignmentViewModel : ObservableObject
{
    private readonly Func<RigRole, string?, string?> _apply;
    private bool _syncing;

    public RigAssignmentViewModel(RigRole role, string title, IReadOnlyList<AssignmentChoice> choices, string? current, Func<RigRole, string?, string?> apply)
    {
        Role = role;
        Title = title;
        Hint = role switch
        {
            RigRole.Camera => "The camera on this telescope. Every imaging setup has one.",
            RigRole.Mount => "Optional. Points the telescope. Imaging setups on one mount can share it.",
            RigRole.Focuser => "Optional. Needed for autofocus.",
            RigRole.FilterWheel => "Optional. Needed to change filters.",
            RigRole.Guider => "Optional. Keeps the telescope on target. Imaging setups can share a guider.",
            _ => "Optional. Turns the camera to frame the target.",
        };
        Choices = choices;
        _apply = apply;
        _syncing = true;
        Selected = choices.FirstOrDefault(c => string.Equals(c.Id, current, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        _syncing = false;
    }

    public RigRole Role { get; }

    public string Title { get; }

    /// <summary>One line on what the role is for, shown under the choice.</summary>
    public string Hint { get; }

    public IReadOnlyList<AssignmentChoice> Choices { get; }

    /// <summary>The device of the role as a choice; setting it changes the rig.</summary>
    [ObservableProperty]
    public partial AssignmentChoice? Selected { get; set; }

    partial void OnSelectedChanged(AssignmentChoice? oldValue, AssignmentChoice? newValue)
    {
        if (_syncing || newValue is null || oldValue is null)
        {
            return;
        }

        if (_apply(Role, newValue.Id) is not null)
        {
            // Refused: the rig is as it was, and so is the choice.
            _syncing = true;
            Selected = oldValue;
            _syncing = false;
        }
    }
}

/// <summary>
/// Managing one rig on its overview: rename it, remove it, and give it its devices. Nothing is connected or moved by any of it, and a device is never taken from another rig. A problem is told
/// on the notice of the equipment page, and the rig stays as it was.
/// </summary>
public sealed partial class RigSetupViewModel : ObservableObject
{
    private readonly RigViewModel _rig;
    private readonly EquipmentService _service;
    private readonly Action<string> _notify;

    public RigSetupViewModel(RigViewModel rig, EquipmentService service, Action<string> notify)
    {
        _rig = rig;
        _service = service;
        _notify = notify;
        NameText = rig.Name;
        var configured = service.FindRig(rig.RigIdText);
        Assignments =
        [
            Assignment(RigRole.Camera, "Camera", false, configured),
            Assignment(RigRole.Mount, "Mount", true, configured),
            Assignment(RigRole.Focuser, "Focuser", true, configured),
            Assignment(RigRole.FilterWheel, "Filter wheel", true, configured),
            Assignment(RigRole.Guider, "Guider", true, configured),
            Assignment(RigRole.Rotator, "Rotator", true, configured),
        ];
    }

    public string RigIdText => _rig.RigIdText;

    /// <summary>The roles of the rig, each with the devices that can play it.</summary>
    public IReadOnlyList<RigAssignmentViewModel> Assignments { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    public partial string NameText { get; set; }

    /// <summary>Remove was asked for once and waits for the second click.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; private set; }

    private RigAssignmentViewModel Assignment(RigRole role, string title, bool optional, RigConfiguration? configured)
    {
        var choices = new List<AssignmentChoice>();
        if (optional)
        {
            choices.Add(new AssignmentChoice(null, "None"));
        }

        var candidates = _service.CandidatesFor(_rig.RigIdText, role).ToList();
        var current = configured?.DeviceFor(role);
        if (current is not null && candidates.All(c => !string.Equals(c.Id, current, StringComparison.OrdinalIgnoreCase)) && _service.Configuration.Find(current) is { } own)
        {
            candidates.Add(own);
        }

        choices.AddRange(candidates.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new AssignmentChoice(c.Id, c.Name)));
        return new RigAssignmentViewModel(role, title, choices, current, Apply);
    }

    // The service decides (and saves); what it refuses is told, and nothing else changes.
    private string? Apply(RigRole role, string? deviceId)
    {
        var result = _service.SetRigDevice(_rig.RigIdText, role, deviceId);
        if (!result.Succeeded)
        {
            _notify(result.Problem ?? "The imaging setup could not be changed.");
            return result.Problem ?? "The imaging setup could not be changed.";
        }

        return null;
    }

    private bool CanRename() => NameText.Trim().Length > 0 && !string.Equals(NameText.Trim(), _rig.Name, StringComparison.Ordinal);

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        var result = _service.RenameRig(_rig.RigIdText, NameText);
        if (!result.Succeeded)
        {
            _notify(result.Problem ?? "The imaging setup could not be renamed.");
        }
    }

    /// <summary>Removes the rig after a second click; its devices stay in the equipment, as they are.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (!IsConfirmingRemove)
        {
            IsConfirmingRemove = true;
            return;
        }

        IsConfirmingRemove = false;
        var result = _service.RemoveRig(_rig.RigIdText);
        if (!result.Succeeded)
        {
            _notify(result.Problem ?? "The imaging setup could not be removed.");
        }
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;
}

/// <summary>Adding a rig: a name and a camera that is in no rig. The rig starts with nothing else; its mount, guider, focuser and the rest are given on its overview.</summary>
public sealed partial class AddRigViewModel : ObservableObject
{
    private readonly EquipmentService _service;
    private readonly Action<string> _notify;
    private readonly Action<string> _opened;

    public AddRigViewModel(EquipmentService service, Action<string> notify, Action<string> opened)
    {
        _service = service;
        _notify = notify;
        _opened = opened;
    }

    /// <summary>The cameras that are in no rig.</summary>
    public ObservableCollection<AssignmentChoice> FreeCameras { get; } = [];

    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NameText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial AssignmentChoice? SelectedCamera { get; set; }

    /// <summary>Why a rig cannot be added now; empty when it can.</summary>
    public string DisabledText => FreeCameras.Count == 0 ? "Every camera is in an imaging setup already, and a setup needs a camera of its own. To make another setup, add another camera first." : string.Empty;

    /// <summary>Reads which cameras are free.</summary>
    public void Refresh()
    {
        var used = _service.Configuration.Rigs.Select(r => r.CameraId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var free = _service.Configuration.Devices.Where(d => d.Type == DeviceType.Camera && !used.Contains(d.Id)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new AssignmentChoice(d.Id, d.Name)).ToList();
        if (!free.SequenceEqual(FreeCameras))
        {
            FreeCameras.Clear();
            foreach (var camera in free)
            {
                FreeCameras.Add(camera);
            }
        }

        SelectedCamera = FreeCameras.FirstOrDefault(c => c.Id == SelectedCamera?.Id) ?? FreeCameras.FirstOrDefault();
        OnPropertyChanged(nameof(DisabledText));
        AddCommand.NotifyCanExecuteChanged();
        BeginCommand.NotifyCanExecuteChanged();
    }

    private bool CanBegin() => _service.Configuration.Devices.Any(d => d.Type == DeviceType.Camera && !_service.Configuration.Rigs.Any(r => string.Equals(r.CameraId, d.Id, StringComparison.OrdinalIgnoreCase)));

    [RelayCommand(CanExecute = nameof(CanBegin))]
    private void Begin()
    {
        if (!CanBegin())
        {
            return;
        }

        Refresh();
        NameText = string.Empty;
        IsAdding = true;
    }

    [RelayCommand]
    private void Cancel() => IsAdding = false;

    private bool CanAdd() => NameText.Trim().Length > 0 && SelectedCamera?.Id is not null;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var name = NameText.Trim();
        var result = _service.AddRig(name, SelectedCamera!.Id!);
        if (!result.Succeeded)
        {
            _notify(result.Problem ?? "The imaging setup could not be added.");
            return;
        }

        IsAdding = false;
        _opened(name);
    }
}
