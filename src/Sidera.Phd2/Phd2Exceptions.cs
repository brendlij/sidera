namespace Sidera.Phd2;

/// <summary>
/// Something went wrong with PHD2. Derives from <see cref="InvalidOperationException"/>, which is what the guider contracts of Sidera say a
/// guider throws when it cannot do what it was asked in the state it is in.
/// </summary>
public class Phd2Exception(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>PHD2 cannot be reached, or the connection to it was lost.</summary>
public sealed class Phd2ConnectionException(string message, Exception? inner = null) : Phd2Exception(message, inner);

/// <summary>PHD2 answered a request with an error.</summary>
public sealed class Phd2RpcException(string method, int code, string message)
    : Phd2Exception($"PHD2 refused '{method}': {message}")
{
    public string Method { get; } = method;

    /// <summary>The error code of the JSON-RPC answer.</summary>
    public int Code { get; } = code;

    /// <summary>The message of PHD2 as it sent it.</summary>
    public string Reason { get; } = message;
}

/// <summary>PHD2 did not answer in time.</summary>
public sealed class Phd2TimeoutException(string message) : Phd2Exception(message);

/// <summary>PHD2 reported that guiding did not settle, for a reason other than the timeout, or that it could not start guiding.</summary>
public sealed class Phd2SettleFailedException(string message) : Phd2Exception(message);
