namespace Astra.Ascom;

/// <summary>
/// How often the adapters ask a driver and how long Astra waits for it. A timeout only ends Astra's waiting: what the
/// hardware does meanwhile is a separate question, which each adapter answers in its error message. None of these
/// operations is ever retried.
/// </summary>
public sealed record AscomTimings
{
    public static AscomTimings Default { get; } = new();

    /// <summary>Opening the connection to the driver: COM activation, USB or serial hardware.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Letting go of a driver: disconnecting and releasing it.</summary>
    public TimeSpan ReleaseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FocuserPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan FocuserMoveTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan MountPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MountSlewTimeout { get; init; } = TimeSpan.FromSeconds(300);

    public TimeSpan CameraPollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>What an exposure may take beyond its duration: readout and download.</summary>
    public TimeSpan CameraDownloadMargin { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long Astra watches a device after it was told to stop (halt, abort), to report where it stands.</summary>
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(10);
}
