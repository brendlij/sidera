using Sidera.Sky;

namespace Sidera.Desktop.Settings;

/// <summary>The few settings of the sky atlas of the framing workspace: which survey it opens with, where and how much of it is cached, and how long the network may take.</summary>
public sealed record SkyAtlasSettings
{
    public string DefaultSurveyId { get; init; } = SkySurveys.DefaultId;

    /// <summary>The folder of the cache; <c>null</c> for the default under the local application data.</summary>
    public string? CacheDirectory { get; init; }

    public int MaxCacheMegabytes { get; init; } = 512;

    public double NetworkTimeoutSeconds { get; init; } = 10;

    public string? Problem =>
        string.IsNullOrWhiteSpace(DefaultSurveyId) ? "Choose a default survey."
        : MaxCacheMegabytes is < 16 or > 100_000 ? "The cache size must be from 16 to 100000 megabytes."
        : !double.IsFinite(NetworkTimeoutSeconds) || NetworkTimeoutSeconds is < 1 or > 300 ? "The network timeout must be from 1 to 300 seconds."
        : null;

    public long MaxCacheBytes => MaxCacheMegabytes * 1024L * 1024L;
}
