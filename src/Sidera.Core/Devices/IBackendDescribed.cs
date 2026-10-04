namespace Sidera.Core.Devices;

/// <summary>
/// Implemented by a device that can say what drives it, so that the user interface does not have to guess from the
/// class name. Devices that do not implement it are shown with the backend their class name suggests.
/// </summary>
public interface IBackendDescribed
{
    /// <summary>The kind of backend: "Simulator" or "ASCOM".</summary>
    string BackendName { get; }

    /// <summary>The stable identifier of the driver within its backend (the ProgId of an ASCOM driver), or <c>null</c>.</summary>
    string? DriverId { get; }
}
