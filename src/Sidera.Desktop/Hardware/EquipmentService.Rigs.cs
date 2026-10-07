using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Microsoft.Extensions.Logging;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Hardware;

/// <summary>
/// Managing the rigs of the equipment: add, rename and remove a rig, give it its devices and its optics. A rig is an imaging train, a camera with what sits on it; its mount and its guider
/// are optional and are not global: two rigs share one when they name the same device. Every change is saved first and then applied to the runtime, all or nothing, and never takes a device
/// from another rig by itself.
/// </summary>
public sealed partial class EquipmentService
{
    /// <summary>The longest name a rig can have.</summary>
    public const int MaxRigNameLength = 48;

    /// <summary>The configuration of a rig by its id; <c>null</c> when there is none.</summary>
    public RigConfiguration? FindRig(string rigId) =>
        _configuration.Rigs.FirstOrDefault(r => string.Equals(r.Id, rigId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Adds a rig around a camera. The camera must be part of the equipment and in no other rig (a camera belongs to one imaging train); the rig has no other device yet and no optics.
    /// A camera whose rig was only made to hold its optics keeps that rig: use <see cref="RenameRig"/> and the assignments on it.
    /// </summary>
    public EquipmentResult AddRig(string name, string cameraId)
    {
        if (RigNameProblem(name, null) is { } nameProblem)
        {
            return EquipmentResult.Fail(nameProblem);
        }

        if (RoleProblem(null, RigRole.Camera, cameraId) is { } cameraProblem)
        {
            return EquipmentResult.Fail(cameraProblem);
        }

        var id = NewRigId(name);
        var rig = new RigConfiguration(id, name.Trim(), cameraId, null, null, null);
        return ApplyRigChange(null, rig, "added");
    }

    /// <summary>Renames a rig. The name only has to be there and different from the other rigs' names.</summary>
    public EquipmentResult RenameRig(string rigId, string name)
    {
        if (FindRig(rigId) is not { } rig)
        {
            return EquipmentResult.Fail("The imaging setup is not part of the equipment.");
        }

        if (RigNameProblem(name, rig.Id) is { } problem)
        {
            return EquipmentResult.Fail(problem);
        }

        return ApplyRigChange(rig, rig with { Name = name.Trim() }, "renamed");
    }

    /// <summary>Removes a rig. Its devices stay in the equipment, exactly as they are; nothing is disconnected.</summary>
    public EquipmentResult RemoveRig(string rigId)
    {
        if (FindRig(rigId) is not { } rig)
        {
            return EquipmentResult.Fail("The imaging setup is not part of the equipment.");
        }

        return ApplyRigChange(rig, null, "removed");
    }

    /// <summary>
    /// Gives a rig the device for a role, or takes the optional device away (<paramref name="deviceId"/> <c>null</c>). The camera cannot be taken away. A device is never taken from another
    /// rig: a camera, focuser, filter wheel or rotator that another rig has is refused; a mount and a guider may be shared, and then both rigs name the same device.
    /// </summary>
    public EquipmentResult SetRigDevice(string rigId, RigRole role, string? deviceId)
    {
        if (FindRig(rigId) is not { } rig)
        {
            return EquipmentResult.Fail("The imaging setup is not part of the equipment.");
        }

        if (deviceId is null && role == RigRole.Camera)
        {
            return EquipmentResult.Fail("An imaging setup needs a camera.");
        }

        if (string.Equals(rig.DeviceFor(role), deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return EquipmentResult.Ok();
        }

        if (deviceId is not null && RoleProblem(rig.Id, role, deviceId) is { } problem)
        {
            return EquipmentResult.Fail(problem);
        }

        return ApplyRigChange(rig, rig.WithDevice(role, deviceId), $"{RoleName(role)} {(deviceId is null ? "removed" : "set")}");
    }

    /// <summary>Sets the optics of a rig (<c>null</c> removes them). Only the inputs are kept.</summary>
    public EquipmentResult SetRigOptics(string rigId, OpticalTrain? optics)
    {
        if (FindRig(rigId) is not { } rig)
        {
            return EquipmentResult.Fail("The imaging setup is not part of the equipment.");
        }

        return ApplyRigChange(rig, rig with { Optics = optics }, optics is null ? "optics removed" : "optics set");
    }

    /// <summary>The rigs that use the device, with the role it has in each; more than one means that the device is shared.</summary>
    public IReadOnlyList<(RigConfiguration Rig, RigRole Role)> RigsOf(string deviceId) =>
    [
        .. _configuration.Rigs.SelectMany(r => Enum.GetValues<RigRole>()
            .Where(role => string.Equals(r.DeviceFor(role), deviceId, StringComparison.OrdinalIgnoreCase)).Select(role => (r, role))),
    ];

    /// <summary>The devices of the equipment that can be given to a rig for a role: of the right kind, and (for a camera, focuser, filter wheel and rotator) not in another rig.</summary>
    public IReadOnlyList<DeviceConfiguration> CandidatesFor(string rigId, RigRole role) =>
    [
        .. _configuration.Devices.Where(d => d.Type == TypeOf(role) && RoleProblem(rigId, role, d.Id) is null),
    ];

    private static DeviceType TypeOf(RigRole role) => role switch
    {
        RigRole.Camera => DeviceType.Camera,
        RigRole.Focuser => DeviceType.Focuser,
        RigRole.FilterWheel => DeviceType.FilterWheel,
        RigRole.Rotator => DeviceType.Rotator,
        RigRole.Mount => DeviceType.Mount,
        RigRole.Guider => DeviceType.Guider,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string RoleName(RigRole role) => role switch
    {
        RigRole.FilterWheel => "filter wheel",
        _ => role.ToString().ToLowerInvariant(),
    };

    // Roles that belong to one rig: a second rig on the same device would be two trains on one camera or focuser. Mounts and guiders are shared by naming them twice.
    private static bool IsExclusive(RigRole role) => role is RigRole.Camera or RigRole.Focuser or RigRole.FilterWheel or RigRole.Rotator;

    private string? RoleProblem(string? rigId, RigRole role, string deviceId)
    {
        if (_configuration.Find(deviceId) is not { } device)
        {
            return $"The {RoleName(role)} is not part of the equipment.";
        }

        if (device.Type != TypeOf(role))
        {
            return $"'{device.Name}' is not a {RoleName(role)}.";
        }

        if (IsExclusive(role) && _configuration.Rigs.FirstOrDefault(r =>
                !string.Equals(r.Id, rigId, StringComparison.OrdinalIgnoreCase) && string.Equals(r.DeviceFor(role), deviceId, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            return $"'{device.Name}' is already the {RoleName(role)} of the imaging setup '{other.Name}'.";
        }

        return null;
    }

    private string? RigNameProblem(string name, string? ownId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "The imaging setup needs a name.";
        }

        if (name.Trim().Length > MaxRigNameLength)
        {
            return $"The name of an imaging setup can have at most {MaxRigNameLength} characters.";
        }

        return _configuration.Rigs.Any(r => !string.Equals(r.Id, ownId, StringComparison.OrdinalIgnoreCase)
                                            && string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? $"An imaging setup named '{name.Trim()}' already exists."
            : null;
    }

    // "rig.main-rig", and "rig.main-rig-2" when that is taken (also by a device or a rig of the runtime).
    private string NewRigId(string name)
    {
        var slug = new string([.. name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        var baseId = "rig." + (slug.Length == 0 ? "rig" : slug);
        if (baseId.Length > EquipmentIds.MaxLength - 4)
        {
            baseId = baseId[..(EquipmentIds.MaxLength - 4)].TrimEnd('-', '.');
        }

        var id = baseId;
        for (var n = 2; _configuration.Rigs.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) || _host.RigRegistry.TryGet(new RigId(id), out _); n++)
        {
            id = $"{baseId}-{n}";
        }

        return id;
    }

    // The one place that changes a rig in the configuration and in the runtime together: saved first, then the registry follows.
    private EquipmentResult ApplyRigChange(RigConfiguration? before, RigConfiguration? after, string what)
    {
        var rigs = _configuration.Rigs.Where(r => r != before).ToList();
        if (after is not null)
        {
            rigs.Add(after);
        }

        var next = _configuration with { Rigs = rigs };
        if (!TrySave(next, out var problem))
        {
            return EquipmentResult.Fail(problem!);
        }

        if (before is not null)
        {
            _host.RigRegistry.Unregister(new RigId(before.Id));
        }

        if (after is not null)
        {
            try
            {
                _host.AddRig(after.ToRig());
            }
            catch (InvalidOperationException ex)
            {
                // The registry refused the rig (a device that is not loaded): put the registry back as it was, and the file with it.
                if (before is not null)
                {
                    _host.AddRig(before.ToRig());
                }

                TrySave(_configuration, out _);
                return EquipmentResult.Fail(ex.Message);
            }
        }

        _configuration = next;
        _logger.LogInformation("The rig {RigId} was changed ({What})", (after ?? before)?.Id, what);
        Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.RigsChanged));
        return EquipmentResult.Ok();
    }
}
