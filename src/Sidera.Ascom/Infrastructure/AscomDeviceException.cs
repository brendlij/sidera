using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Sidera.Core.Devices;

namespace Sidera.Ascom.Infrastructure;

/// <summary>
/// An operation on an ASCOM device failed. The message is written for the user and names the device and the driver;
/// the exception that the driver threw is kept as <see cref="Exception.InnerException"/> for the log.
/// </summary>
public class AscomDeviceException : Exception
{
    public AscomDeviceException(DeviceId deviceId, string progId, string operation, string message, Exception? inner = null)
        : base(message, inner)
    {
        DeviceId = deviceId;
        ProgId = progId;
        Operation = operation;
    }

    public DeviceId DeviceId { get; }
    public string ProgId { get; }

    /// <summary>What was attempted: "connect", "move", "slew", "expose" ...</summary>
    public string Operation { get; }
}

/// <summary>The driver or the device cannot do what Sidera needs (a relative-only focuser, a mount that cannot slew).</summary>
public sealed class AscomUnsupportedException(DeviceId deviceId, string progId, string operation, string message)
    : AscomDeviceException(deviceId, progId, operation, message);

/// <summary>An operation did not finish in the time Sidera waits for it. Whether the hardware stopped is stated in the message.</summary>
public sealed class AscomTimeoutException(DeviceId deviceId, string progId, string operation, string message)
    : AscomDeviceException(deviceId, progId, operation, message);

/// <summary>Turns what ASCOM drivers throw into <see cref="AscomDeviceException"/>.</summary>
public static class AscomErrors
{
    /// <summary>
    /// The exception as the user should read it. Cancellation and exceptions that already are Sidera's own pass through
    /// unchanged; so do argument and state errors raised by the adapters, and an <see cref="ObjectDisposedException"/>,
    /// which means Sidera itself shut the device down.
    /// </summary>
    public static Exception Translate(Exception exception, DeviceId deviceId, string deviceName, string progId, string operation)
    {
        var ex = Unwrap(exception);
        if (ex is OperationCanceledException or AscomDeviceException or ObjectDisposedException or ArgumentException)
        {
            return ex;
        }

        // An InvalidOperationException of an adapter is Sidera's own; one of the ASCOM library is the driver's.
        if (ex is InvalidOperationException && ex.GetType().Namespace != "ASCOM")
        {
            return ex;
        }

        return new AscomDeviceException(
            deviceId, progId, operation, $"Could not {operation} {deviceName} ({progId}): {Describe(ex)}", ex);
    }

    /// <summary>One sentence about what the driver reported.</summary>
    public static string Describe(Exception exception)
    {
        var ex = Unwrap(exception);
        var type = ex.GetType();
        var text = FirstLine(ex.Message);

        // The ASCOM exception library lives in the namespace ASCOM; its classes are matched by name so that the
        // meaning is kept whichever version of the library the driver was built against.
        if (type.Namespace == "ASCOM")
        {
            return type.Name switch
            {
                "NotConnectedException" => $"the driver says it is not connected ({text})",
                "NotImplementedException" or "MethodNotImplementedException" or "PropertyNotImplementedException" =>
                    $"the driver does not support this ({text})",
                "InvalidValueException" => $"the driver rejected a value ({text})",
                "InvalidOperationException" => $"the driver refused the operation in its current state ({text})",
                "ParkedException" => $"the mount is parked ({text})",
                "SlavedException" => $"the device is slaved to another ({text})",
                "ValueNotSetException" => $"the driver has no value for this yet ({text})",
                "DriverException" => string.Create(CultureInfo.InvariantCulture, $"the driver reported error 0x{ex.HResult:X8}: {text}"),
                _ => $"{type.Name}: {text}",
            };
        }

        return ex switch
        {
            COMException com => string.Create(CultureInfo.InvariantCulture, $"COM error 0x{com.HResult:X8}: {text}"),
            _ => $"{type.Name}: {text}",
        };
    }

    private static Exception Unwrap(Exception exception)
    {
        var ex = exception;
        while (true)
        {
            switch (ex)
            {
                case TargetInvocationException { InnerException: { } inner }:
                    ex = inner;
                    break;
                case AggregateException { InnerExceptions.Count: 1 } aggregate:
                    ex = aggregate.InnerExceptions[0];
                    break;
                default:
                    return ex;
            }
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "no details" : line;
    }
}
