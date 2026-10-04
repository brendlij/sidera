using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Desktop.Hardware;

/// <summary>The outcome of changing the equipment: it worked, or a sentence says why not and nothing was changed.</summary>
public sealed record EquipmentResult(bool Succeeded, string? Problem, IDevice? Device = null)
{
    public static EquipmentResult Ok(IDevice? device = null) => new(true, null, device);

    public static EquipmentResult Fail(string problem) => new(false, problem);
}

public enum EquipmentChangeKind
{
    DeviceAdded,
    DeviceReplaced,
    DeviceRemoved,
    RigsAdded,
}

/// <summary>What changed, for the pages that show the equipment.</summary>
public sealed record EquipmentChange(EquipmentChangeKind Kind, string? DeviceId = null, IDevice? Device = null, IReadOnlyList<Rig>? Rigs = null);

/// <summary>
/// The equipment of the installation, kept in step with the runtime: it loads the equipment file at startup (every device
/// is created and registered, none is connected), and adds, replaces and removes devices while Sidera runs, saving the file
/// after each change. Devices of every backend are registered with the host through the factories; nothing above the
/// registry knows which backend a device has.
/// <para>
/// A change is all or nothing: when a device cannot be created or the file cannot be saved, the runtime and the file are as
/// they were. A device that cannot be changed because it is connected, part of a rig or in use says why.
/// </para>
/// </summary>
public sealed class EquipmentService : IDevicePreferenceStore
{
    private readonly SideraRuntimeHost _host;
    private readonly EquipmentConfigurationStore _store;
    private readonly DeviceFactoryRegistry _factories;
    private readonly ILogger _logger;
    private EquipmentConfiguration _configuration = EquipmentConfiguration.Empty;
    private readonly List<string> _problems = [];

    public EquipmentService(
        SideraRuntimeHost host, EquipmentConfigurationStore store, DeviceFactoryRegistry factories, ILogger? logger = null)
    {
        _host = host;
        _store = store;
        _factories = factories;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The equipment as it is stored, including devices that could not be created on this run.</summary>
    public EquipmentConfiguration Configuration => _configuration;

    /// <summary>What went wrong while loading: an unreadable file, a device that could not be created. Empty when all is well.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Raised on the calling thread after a change was made and saved.</summary>
    public event EventHandler<EquipmentChange>? Changed;

    public string FilePath => _store.Path;

    public DeviceConfiguration? ConfigurationOf(string id) => _configuration.Find(id);

    /// <summary>
    /// Reads the equipment file and creates its devices and rigs. Nothing is connected. A file that cannot be read leaves
    /// the equipment empty and is not overwritten until the next change, which keeps a copy of it first.
    /// </summary>
    public void Load()
    {
        _problems.Clear();
        EquipmentConfiguration loaded;
        try
        {
            loaded = _store.Load();
        }
        catch (EquipmentConfigurationException ex)
        {
            _logger.LogError(ex, "The equipment file {Path} could not be used", _store.Path);
            _problems.Add($"{ex.Message} Sidera starts without equipment; the file is kept as it is.");
            return;
        }

        _configuration = loaded;
        foreach (var device in loaded.Devices)
        {
            SyncAcquisitionDefaults(device);
            try
            {
                _factories.CreateAndAdd(_host, device);
                _logger.LogInformation(
                    "Device {DeviceId} ({Type}, {Backend}) loaded, disconnected", device.Id, device.Type, DeviceBackends.Name(device.Backend));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device {DeviceId} could not be created", device.Id);
                _problems.Add($"The device '{device.Name}' ({device.Id}) could not be loaded: {ex.Message.Split('\n', 2)[0]}");
            }
        }

        foreach (var rig in loaded.Rigs)
        {
            try
            {
                CreateRig(rig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rig {RigId} could not be created", rig.Id);
                _problems.Add($"The rig '{rig.Name}' ({rig.Id}) could not be loaded: {ex.Message.Split('\n', 2)[0]}");
            }
        }
    }

    /// <summary>A sentence about why the device cannot be edited now, or <c>null</c> when it can.</summary>
    public string? WhyCannotEdit(string id)
    {
        if (!_host.DeviceRegistry.TryGet(new DeviceId(id), out var device) || device is null)
        {
            return "The device is not loaded.";
        }

        return device.ConnectionState == DeviceConnectionState.Disconnected ? null : "Disconnect the device to change it.";
    }

    /// <summary>A sentence about why the device cannot be removed now, or <c>null</c> when it can.</summary>
    public string? WhyCannotRemove(string id)
    {
        if (WhyCannotEdit(id) is { } problem)
        {
            return problem;
        }

        return _host.RigRegistry.GetAll().FirstOrDefault(r => r.CameraId.Value == id || r.FocuserId?.Value == id || r.FilterWheelId?.Value == id)
            is { } rig
            ? $"It is part of the rig '{rig.Name}'."
            : null;
    }

    public EquipmentResult Add(DeviceConfiguration configuration)
    {
        if (Validate(configuration, isNew: true) is { } invalid)
        {
            return EquipmentResult.Fail(invalid);
        }

        IDevice device;
        try
        {
            device = _factories.CreateAndAdd(_host, configuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device {DeviceId} could not be added", configuration.Id);
            return EquipmentResult.Fail($"The device could not be added: {ex.Message.Split('\n', 2)[0]}");
        }

        var next = _configuration.With(configuration);
        if (!TrySave(next, out var saveProblem))
        {
            _host.RemoveDevice(configuration.DeviceId);
            return EquipmentResult.Fail(saveProblem!);
        }

        _configuration = next;
        _logger.LogInformation(
            "Device {DeviceId} ({Type}, {Backend}) added", configuration.Id, configuration.Type, DeviceBackends.Name(configuration.Backend));
        Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.DeviceAdded, configuration.Id, device));
        return EquipmentResult.Ok(device);
    }

    /// <summary>Changes the name or the driver of a device that is not connected. Its id, its kind and its backend stay.</summary>
    public EquipmentResult Update(DeviceConfiguration configuration)
    {
        var existing = _configuration.Find(configuration.Id);
        if (existing is null)
        {
            return EquipmentResult.Fail("The device is not part of the equipment.");
        }

        if (existing.Type != configuration.Type || existing.Backend != configuration.Backend)
        {
            return EquipmentResult.Fail("The kind and the backend of a device cannot be changed; add a new device instead.");
        }

        if (WhyCannotEdit(existing.Id) is { } cannot)
        {
            return EquipmentResult.Fail(cannot);
        }

        if (Validate(configuration with { Id = existing.Id }, isNew: false) is { } invalid)
        {
            return EquipmentResult.Fail(invalid);
        }

        // The form does not know the preferences; a change of name or driver keeps them.
        configuration = configuration with
        {
            Id = existing.Id,
            Preferences = configuration.Preferences.Count == 0 ? existing.Preferences : configuration.Preferences,
        };
        _host.DeviceRegistry.TryGet(configuration.DeviceId, out var old);
        _host.DeviceRegistry.Unregister(configuration.DeviceId);
        IDevice device;
        try
        {
            device = _factories.CreateAndAdd(_host, configuration);
        }
        catch (Exception ex)
        {
            _host.DeviceRegistry.Register(old!);
            _logger.LogError(ex, "Device {DeviceId} could not be changed", configuration.Id);
            return EquipmentResult.Fail($"The device could not be changed: {ex.Message.Split('\n', 2)[0]}");
        }

        var next = _configuration.With(configuration);
        if (!TrySave(next, out var saveProblem))
        {
            _host.DeviceRegistry.Unregister(configuration.DeviceId);
            _host.DeviceRegistry.Register(old!);
            return EquipmentResult.Fail(saveProblem!);
        }

        _configuration = next;
        SyncAcquisitionDefaults(configuration);
        _ = EndAsync(old);
        _logger.LogInformation("Device {DeviceId} changed", configuration.Id);
        Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.DeviceReplaced, configuration.Id, device));
        return EquipmentResult.Ok(device);
    }

    // The runtime reads what each camera normally takes frames with from here: what the equipment file keeps as preferences.
    private void SyncAcquisitionDefaults(DeviceConfiguration device)
    {
        if (device.Type == DeviceType.Camera)
        {
            _host.AcquisitionDefaults.Set(device.DeviceId, DevicePreferences.AcquisitionDefaults(device.Preferences));
        }
    }

    public IReadOnlyDictionary<string, string> GetPreferences(string deviceId) =>
        _configuration.Find(deviceId)?.Preferences ?? DeviceConfiguration.NoSettings;

    /// <summary>Stores the preferences of a device. Allowed while it is connected: nothing about the device is rebuilt.</summary>
    public string? SavePreferences(string deviceId, IReadOnlyDictionary<string, string> preferences)
    {
        var existing = _configuration.Find(deviceId);
        if (existing is null)
        {
            return "The device is not part of the equipment, so its preferences are not kept.";
        }

        var next = _configuration.With(existing with { Preferences = preferences });
        if (!TrySave(next, out var problem))
        {
            return problem;
        }

        _configuration = next;
        SyncAcquisitionDefaults(existing with { Preferences = preferences });
        return null;
    }

    public EquipmentResult Remove(string id)
    {
        if (_configuration.Find(id) is null)
        {
            return EquipmentResult.Fail("The device is not part of the equipment.");
        }

        if (WhyCannotRemove(id) is { } cannot)
        {
            return EquipmentResult.Fail(cannot);
        }

        var next = _configuration.Without(id);
        if (!TrySave(next, out var saveProblem))
        {
            return EquipmentResult.Fail(saveProblem!);
        }

        _host.DeviceRegistry.TryGet(new DeviceId(id), out var device);
        _host.RemoveDevice(new DeviceId(id));
        _host.AcquisitionDefaults.Set(new DeviceId(id), null);
        _configuration = next;
        _ = EndAsync(device);
        _logger.LogInformation("Device {DeviceId} removed", id);
        Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.DeviceRemoved, id));
        return EquipmentResult.Ok();
    }

    /// <summary>
    /// Adds the demo: the simulated devices of the standalone demo and the three rigs around them, all of them stored like
    /// any other equipment. Refused, and nothing is added, when one of their ids is already in use.
    /// </summary>
    public EquipmentResult AddDemoEquipment(EquipmentConfiguration? demo = null)
    {
        demo ??= DemoSetup.Configuration();
        var taken = demo.Devices.Select(d => d.Id).Concat(demo.Rigs.Select(r => r.Id))
            .Where(id => _configuration.Find(id) is not null || _host.DeviceRegistry.TryGet(new DeviceId(id), out _)
                || _host.RigRegistry.TryGet(new RigId(id), out _))
            .ToList();
        if (taken.Count > 0)
        {
            return EquipmentResult.Fail($"The demo cannot be added: the id '{taken[0]}' is already in use.");
        }

        var added = new List<(DeviceConfiguration Configuration, IDevice Device)>();
        var rigs = new List<Rig>();
        try
        {
            foreach (var device in demo.Devices)
            {
                added.Add((device, _factories.CreateAndAdd(_host, device)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The demo equipment could not be added");
            foreach (var (device, _) in added)
            {
                _host.RemoveDevice(device.DeviceId);
            }

            return EquipmentResult.Fail($"The demo could not be added: {ex.Message.Split('\n', 2)[0]}");
        }

        var next = new EquipmentConfiguration([.. _configuration.Devices, .. demo.Devices], [.. _configuration.Rigs, .. demo.Rigs]);
        if (!TrySave(next, out var saveProblem))
        {
            foreach (var (device, _) in added)
            {
                _host.RemoveDevice(device.DeviceId);
            }

            return EquipmentResult.Fail(saveProblem!);
        }

        _configuration = next;
        foreach (var rig in demo.Rigs)
        {
            rigs.Add(CreateRig(rig));
        }

        _logger.LogInformation("Demo equipment added: {Devices} devices, {Rigs} rigs", demo.Devices.Count, demo.Rigs.Count);
        foreach (var (device, instance) in added)
        {
            Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.DeviceAdded, device.Id, instance));
        }

        Changed?.Invoke(this, new EquipmentChange(EquipmentChangeKind.RigsAdded, Rigs: rigs));
        return EquipmentResult.Ok();
    }

    private Rig CreateRig(RigConfiguration configuration)
    {
        var rig = new Rig(
            new RigId(configuration.Id),
            configuration.Name,
            new DeviceId(configuration.CameraId),
            configuration.Optics,
            configuration.FocuserId is { } focuser ? new DeviceId(focuser) : null,
            configuration.FilterWheelId is { } wheel ? new DeviceId(wheel) : null);
        _host.AddRig(rig);
        if (configuration.SimulatedBestFocus is { } best)
        {
            _host.AddSimulatedFocusModel(rig.Id, new SimulatedFocusModel(best));
        }

        return rig;
    }

    private string? Validate(DeviceConfiguration configuration, bool isNew)
    {
        if (EquipmentIds.Problem(configuration.Id) is { } idProblem)
        {
            return idProblem;
        }

        if (string.IsNullOrWhiteSpace(configuration.Name))
        {
            return "The device needs a name.";
        }

        if (isNew && (_configuration.Find(configuration.Id) is not null || _host.DeviceRegistry.TryGet(configuration.DeviceId, out _)))
        {
            return $"A device with the id '{configuration.Id}' already exists.";
        }

        if (!_factories.Has(configuration.Backend))
        {
            return $"The {DeviceBackends.Name(configuration.Backend)} backend is not available.";
        }

        if (configuration.Backend == DeviceBackend.Ascom)
        {
            if (!Sidera.Ascom.AscomDeviceFactory.Supports(configuration.Type))
            {
                return $"ASCOM {configuration.Type} devices are not supported yet.";
            }

            if (string.IsNullOrWhiteSpace(configuration.ProgId))
            {
                return "Choose an ASCOM driver, or enter its ProgId.";
            }
        }

        return null;
    }

    private bool TrySave(EquipmentConfiguration next, out string? problem)
    {
        try
        {
            _store.Save(next);
            problem = null;
            return true;
        }
        catch (EquipmentConfigurationException ex)
        {
            _logger.LogError(ex, "The equipment could not be saved");
            problem = ex.Message;
            return false;
        }
    }

    // A device that left the runtime lets go of what it holds (an ASCOM device holds nothing while disconnected).
    private async Task EndAsync(IDevice? device)
    {
        try
        {
            if (device is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A device that was removed did not end cleanly");
        }
    }
}
