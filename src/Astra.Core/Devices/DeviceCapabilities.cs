namespace Astra.Core.Devices;

/// <summary>Whether what a device supports is known.</summary>
public enum CapabilityStatus
{
    /// <summary>
    /// Not known: the device is not connected (a driver cannot say before it is), or it was not asked yet. Nothing may be
    /// assumed, neither that a feature exists nor that it does not.
    /// </summary>
    Unknown,

    /// <summary>The device was connected and asked: <see cref="DeviceCapabilities{T}.Value"/> says what it supports.</summary>
    Available,
}

/// <summary>
/// What a device supports, as a snapshot: either <see cref="CapabilityStatus.Unknown"/> (no value) or a value that was read
/// from the connected device. Capabilities are about the device and nothing else: what the user wants (settings,
/// preferences) and what the device is doing now (telemetry, state) are other things, kept in other types.
/// </summary>
public sealed record DeviceCapabilities<T> where T : class
{
    private DeviceCapabilities(CapabilityStatus status, T? value)
    {
        Status = status;
        Value = value;
    }

    public CapabilityStatus Status { get; }

    /// <summary>What the device supports; <c>null</c> while the status is <see cref="CapabilityStatus.Unknown"/>.</summary>
    public T? Value { get; }

    public bool IsAvailable => Status == CapabilityStatus.Available && Value is not null;

    public static DeviceCapabilities<T> Unknown { get; } = new(CapabilityStatus.Unknown, null);

    public static DeviceCapabilities<T> Of(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DeviceCapabilities<T>(CapabilityStatus.Available, value);
    }
}

/// <summary>
/// A device that can say what it supports. The capabilities are <see cref="CapabilityStatus.Unknown"/> while it is not
/// connected, are read when the connection was made, and are read again after every reconnect: drivers may support other
/// things after another connection (another camera behind the same driver, other firmware).
/// </summary>
public interface ICapable<T> where T : class
{
    DeviceCapabilities<T> Capabilities { get; }

    /// <summary>Raised when <see cref="Capabilities"/> changed: after a connect (probed) and after a disconnect (unknown again).</summary>
    event EventHandler? CapabilitiesChanged;
}

/// <summary>A device whose current state beyond its connection (temperatures, tracking, positions) can be read and observed.</summary>
public interface IObservableDevice
{
    /// <summary>Reads the state of the device again. Only reads: it never changes anything on the hardware.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after the state of the device changed (an operation, a refresh).</summary>
    event EventHandler? StateChanged;
}

/// <summary>
/// What a driver says about itself: strings for the user, never used to decide what the device can do. Capabilities come
/// from capability flags and probed members, not from names.
/// </summary>
public sealed record DriverMetadata(
    string? Name = null,
    string? Description = null,
    string? DriverInfo = null,
    string? DriverVersion = null,
    int? InterfaceVersion = null);

/// <summary>
/// A whole-number setting of a device (a gain, an offset) and what values it takes: a range of values, or a list of
/// named choices whose value is the index of the choice. Which of the two a device has is part of its capabilities.
/// </summary>
public sealed record IntegerControl
{
    private IntegerControl(int? minimum, int? maximum, IReadOnlyList<string> choices)
    {
        Minimum = minimum;
        Maximum = maximum;
        Choices = choices;
    }

    public int? Minimum { get; }
    public int? Maximum { get; }

    /// <summary>The names of the choices; empty for a range.</summary>
    public IReadOnlyList<string> Choices { get; }

    public bool IsList => Choices.Count > 0;

    public static IntegerControl Range(int minimum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
        return new IntegerControl(minimum, maximum, []);
    }

    public static IntegerControl List(IEnumerable<string> choices)
    {
        var list = choices.ToList();
        ArgumentOutOfRangeException.ThrowIfZero(list.Count);
        return new IntegerControl(0, list.Count - 1, list);
    }

    public bool Accepts(int value) => value >= Minimum && value <= Maximum;

    public string Describe(int value) => IsList && value >= 0 && value < Choices.Count ? Choices[value] : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
