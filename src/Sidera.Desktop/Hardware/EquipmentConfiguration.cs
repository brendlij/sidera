using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Hardware;

/// <summary>What drives a device. The name is what the equipment file stores.</summary>
public enum DeviceBackend
{
    /// <summary>One of Sidera's own simulated devices.</summary>
    Simulator,

    /// <summary>A driver of the ASCOM Platform, addressed by its ProgId.</summary>
    Ascom,
}

public static class DeviceBackends
{
    public static string Name(DeviceBackend backend) => backend == DeviceBackend.Ascom ? "ASCOM" : "Simulator";

    public static bool TryParse(string? name, out DeviceBackend backend)
    {
        switch (name)
        {
            case "Simulator":
                backend = DeviceBackend.Simulator;
                return true;
            case "ASCOM":
                backend = DeviceBackend.Ascom;
                return true;
            default:
                backend = default;
                return false;
        }
    }
}

/// <summary>
/// One device of the equipment, as it is stored: its identity in Sidera (the id that sequences and rigs refer to), the
/// name it is shown with, what kind of device it is, and which backend drives it with that backend's own settings.
/// Nothing about the state of the device is part of it: whether it is connected is never stored.
/// </summary>
/// <param name="Id">The device id; what <c>.astraseq</c> files refer to. It never says anything about the backend.</param>
/// <param name="Settings">The settings of the backend: <c>progId</c> and <c>driverName</c> for ASCOM; for simulators some device kinds have a few.</param>
public sealed record DeviceConfiguration(
    string Id,
    string Name,
    DeviceType Type,
    DeviceBackend Backend,
    IReadOnlyDictionary<string, string> Settings)
{
    public const string ProgIdKey = "progId";
    public const string DriverNameKey = "driverName";

    public static IReadOnlyDictionary<string, string> NoSettings { get; } = new Dictionary<string, string>();

    public DeviceId DeviceId => new(Id);

    /// <summary>
    /// What the user prefers for the device (a gain, a binning, a tracking rate), as text by key. Applied after every connect
    /// to what the device supports; never a capability and never state. Kept next to the backend settings, apart from sequences.
    /// </summary>
    public IReadOnlyDictionary<string, string> Preferences { get; init; } = NoSettings;

    /// <summary>The ASCOM ProgId, the stable identifier of the driver; <c>null</c> for other backends.</summary>
    public string? ProgId => Settings.TryGetValue(ProgIdKey, out var progId) ? progId : null;

    public string? DriverName => Settings.TryGetValue(DriverNameKey, out var name) ? name : null;

    /// <summary>The setting, or <paramref name="fallback"/> when there is none.</summary>
    public string? Setting(string key, string? fallback = null) => Settings.TryGetValue(key, out var value) ? value : fallback;

    public static DeviceConfiguration Ascom(string id, string name, DeviceType type, string progId, string? driverName = null)
    {
        var settings = new Dictionary<string, string> { [ProgIdKey] = progId };
        if (!string.IsNullOrWhiteSpace(driverName))
        {
            settings[DriverNameKey] = driverName;
        }

        return new DeviceConfiguration(id, name, type, DeviceBackend.Ascom, settings);
    }

    public static DeviceConfiguration Simulator(
        string id, string name, DeviceType type, IReadOnlyDictionary<string, string>? settings = null) =>
        new(id, name, type, DeviceBackend.Simulator, settings ?? NoSettings);
}

/// <summary>An optical train as the equipment file describes it, with the rig that owns it.</summary>
/// <param name="SimulatedBestFocus">For rigs of simulated equipment: where the simulated optics are in focus. Not part of real rigs.</param>
public sealed record RigConfiguration(
    string Id,
    string Name,
    string CameraId,
    string? FocuserId,
    string? FilterWheelId,
    OpticalTrain Optics,
    int? SimulatedBestFocus = null);

/// <summary>
/// The equipment of one installation: its devices and the rigs that group them. Backend-neutral: a rig names device ids and
/// does not know whether a device is simulated or ASCOM. Kept apart from sequence files, which refer to device ids only.
/// </summary>
public sealed record EquipmentConfiguration(IReadOnlyList<DeviceConfiguration> Devices, IReadOnlyList<RigConfiguration> Rigs)
{
    // The format name from before the product was called Sidera. Kept: existing equipment files stay readable, and a new name
    // would need a compatibility layer and no change of the format would come of it.
    public const string FormatId = "astra-equipment";
    public const int CurrentVersion = 1;

    public static EquipmentConfiguration Empty { get; } = new([], []);

    public DeviceConfiguration? Find(string id) =>
        Devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public EquipmentConfiguration With(DeviceConfiguration device) =>
        this with { Devices = [.. Devices.Where(d => !string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)), device] };

    public EquipmentConfiguration Without(string id) =>
        this with { Devices = [.. Devices.Where(d => !string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))] };

    /// <summary>The rigs that name the device.</summary>
    public IEnumerable<RigConfiguration> RigsUsing(string deviceId) => Rigs.Where(r =>
        string.Equals(r.CameraId, deviceId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(r.FocuserId, deviceId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(r.FilterWheelId, deviceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The rules for the id of a device or a rig: what is safe to put in a file name, a log and a sequence.</summary>
public static class EquipmentIds
{
    public const int MaxLength = 64;

    /// <summary>A sentence about what is wrong with the id, or <c>null</c> when it is fine.</summary>
    public static string? Problem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "The device id is required.";
        }

        if (id.Length > MaxLength)
        {
            return $"The device id can have at most {MaxLength} characters.";
        }

        if (!char.IsAsciiLetterOrDigit(id[0]) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return "The device id can only contain letters, digits, '.', '_' and '-', and starts with a letter or digit.";
        }

        return null;
    }

    /// <summary>A suggestion for an id from a type and a name: "camera.main-camera".</summary>
    public static string Suggest(DeviceType type, string? name)
    {
        var prefix = type switch
        {
            DeviceType.FilterWheel => "filterwheel",
            var other => other.ToString().ToLowerInvariant(),
        };

        var slug = new string((name ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? prefix : $"{prefix}.{(slug.Length > 40 ? slug[..40].TrimEnd('-') : slug)}";
    }
}
