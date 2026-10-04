using Sidera.Core.Devices;

namespace Sidera.Ascom.Infrastructure;

/// <summary>
/// Holds the capabilities of one device: unknown until a connection probed them, unknown again after it ended. Raising
/// the event is done outside the lock, so a handler may read the value.
/// </summary>
public sealed class CapabilityHolder<T> where T : class
{
    private readonly object _gate = new();
    private DeviceCapabilities<T> _current = DeviceCapabilities<T>.Unknown;

    public event EventHandler? Changed;

    public DeviceCapabilities<T> Current
    {
        get { lock (_gate) { return _current; } }
    }

    public void Set(T value, object sender) => Replace(DeviceCapabilities<T>.Of(value), sender);

    public void Reset(object sender) => Replace(DeviceCapabilities<T>.Unknown, sender);

    private void Replace(DeviceCapabilities<T> next, object sender)
    {
        lock (_gate)
        {
            _current = next;
        }

        Changed?.Invoke(sender, EventArgs.Empty);
    }
}

/// <summary>
/// Reads optional members of a driver without trusting them: a member the driver does not implement, or one that throws,
/// is "not offered" and is noted for the log and the details of the device, never fatal. Only reads are probed; nothing
/// that changes hardware is ever called to find out what is supported.
/// </summary>
public sealed class CapabilityProbe(string deviceName)
{
    private readonly List<string> _notes = [];

    public IReadOnlyList<string> Notes => _notes;

    public T Read<T>(string member, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _notes.Add($"{member}: {AscomErrors.Describe(ex)}");
            return fallback;
        }
    }

    public T? Try<T>(string member, Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _notes.Add($"{member}: {AscomErrors.Describe(ex)}");
            return null;
        }
    }

    public T? TryRef<T>(string member, Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _notes.Add($"{member}: {AscomErrors.Describe(ex)}");
            return null;
        }
    }

    public override string ToString() => $"{deviceName}: {_notes.Count} probe note(s)";
}
