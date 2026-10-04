using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sidera.Ascom;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Runtime;

namespace Sidera.Desktop.Hardware;

/// <summary>
/// Turns the configuration of a device into a device of one backend and registers it with the host (through the host's
/// own registration: <see cref="SideraRuntimeHost.AddDevice"/>, or the simulator helpers that call it and wire the
/// simulation). Creating a device never connects it and never touches hardware.
/// </summary>
public interface IDeviceFactory
{
    DeviceBackend Backend { get; }

    /// <exception cref="NotSupportedException">The backend has no device of that kind.</exception>
    /// <exception cref="InvalidOperationException">The id is already registered.</exception>
    IDevice CreateAndAdd(SideraRuntimeHost host, DeviceConfiguration configuration);
}

/// <summary>The simulators: every kind of device Sidera has one for. A few kinds read settings, with the demo's values as defaults.</summary>
public sealed class SimulatorDeviceFactory(DemoOptions? options = null) : IDeviceFactory
{
    public const string StartPositionKey = "startPosition";
    public const string MaxPositionKey = "maxPosition";
    public const string FiltersKey = "filters";

    public static IReadOnlyList<string> DefaultFilters { get; } = ["L", "R", "G", "B", "Ha", "OIII", "SII"];

    private readonly DemoOptions _options = options ?? new DemoOptions();

    public DeviceBackend Backend => DeviceBackend.Simulator;

    public IDevice CreateAndAdd(SideraRuntimeHost host, DeviceConfiguration configuration)
    {
        var id = configuration.DeviceId;
        var name = configuration.Name;
        return configuration.Type switch
        {
            DeviceType.Camera => host.AddSimulatedCamera(id, name),
            DeviceType.Mount => host.AddSimulatedMount(id, name, _options.SlewDuration),
            DeviceType.Guider => host.AddSimulatedGuider(
                id, name, _options.GuiderStartDuration, _options.GuiderStopDuration, _options.GuiderDitherDuration),
            DeviceType.Focuser => host.AddSimulatedFocuser(
                id, name,
                startPosition: IntSetting(configuration, StartPositionKey, 10000),
                minPosition: 0,
                maxPosition: IntSetting(configuration, MaxPositionKey, 50000),
                stepsPerSecond: _options.FocuserStepsPerSecond,
                minimumMoveDuration: _options.FocuserMinimumMoveDuration),
            DeviceType.FilterWheel => host.AddSimulatedFilterWheel(
                id, name, Slots(configuration), moveDuration: _options.FilterWheelMoveDuration),
            _ => throw new NotSupportedException($"There is no simulated {configuration.Type}."),
        };
    }

    private static int IntSetting(DeviceConfiguration configuration, string key, int fallback) =>
        configuration.Setting(key) is { } text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static List<FilterSlot> Slots(DeviceConfiguration configuration)
    {
        var names = configuration.Setting(FiltersKey) is { } text
            ? text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [.. DefaultFilters];
        return [.. (names.Length == 0 ? [.. DefaultFilters] : names).Select((n, i) => new FilterSlot(i, n))];
    }
}

/// <summary>The ASCOM backend: a camera, a mount or a focuser through the driver named by the ProgId.</summary>
public sealed class AscomBackendFactory(AscomDeviceFactory factory) : IDeviceFactory
{
    public DeviceBackend Backend => DeviceBackend.Ascom;

    public IDevice CreateAndAdd(SideraRuntimeHost host, DeviceConfiguration configuration)
    {
        var progId = configuration.ProgId
            ?? throw new InvalidOperationException($"The ASCOM device '{configuration.Id}' has no ProgId.");
        var device = factory.Create(configuration.Type, configuration.DeviceId, configuration.Name, progId, host.EventBus);
        host.AddDevice(device);
        return device;
    }
}

/// <summary>PHD2: a guider that talks to a running PHD2 through the host and port of its settings. Creating it never connects to PHD2.</summary>
public sealed class Phd2BackendFactory(Microsoft.Extensions.Logging.ILoggerFactory? loggers = null) : IDeviceFactory
{
    public DeviceBackend Backend => DeviceBackend.Phd2;

    public IDevice CreateAndAdd(SideraRuntimeHost host, DeviceConfiguration configuration)
    {
        if (configuration.Type != DeviceType.Guider)
        {
            throw new NotSupportedException($"PHD2 is a guider; there is no PHD2 {configuration.Type}.");
        }

        var endpoint = Sidera.Phd2.Phd2Endpoint.FromSettings(configuration.Settings);
        var guider = new Sidera.Phd2.Phd2Guider(
            configuration.DeviceId, configuration.Name, endpoint, host.EventBus, loggers?.CreateLogger($"Sidera.Phd2.{configuration.Id}"));
        host.AddDevice(guider);
        return guider;
    }
}

/// <summary>The backends Sidera can create devices of, by name. Simulator and ASCOM devices live side by side in one host.</summary>
public sealed class DeviceFactoryRegistry(IEnumerable<IDeviceFactory> factories)
{
    private readonly Dictionary<DeviceBackend, IDeviceFactory> _factories = factories.ToDictionary(f => f.Backend);

    public bool Has(DeviceBackend backend) => _factories.ContainsKey(backend);

    /// <exception cref="NotSupportedException">No factory for the backend, or the backend has no such device.</exception>
    public IDevice CreateAndAdd(SideraRuntimeHost host, DeviceConfiguration configuration) =>
        _factories.TryGetValue(configuration.Backend, out var factory)
            ? factory.CreateAndAdd(host, configuration)
            : throw new NotSupportedException($"The {DeviceBackends.Name(configuration.Backend)} backend is not available.");
}
