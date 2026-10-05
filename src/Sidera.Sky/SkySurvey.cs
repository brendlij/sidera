using Sidera.Core.Framing;

namespace Sidera.Sky;

/// <summary>
/// What a survey says about itself and about the rights to its images, as the provider published it. Kept as published and stored beside the cached tiles, so that
/// a cache is never taken for imagery of Sidera, and so that a later offline pack can be offered only for a survey whose terms allow it. Sidera takes no
/// position on the rights: a value that is not published is <c>null</c>.
/// </summary>
public sealed record SkySurveyInfo
{
    /// <summary>The identifier of the survey ("CDS/P/DSS2/color").</summary>
    public required string Id { get; init; }

    public string? Title { get; init; }

    /// <summary>Where the survey is served from.</summary>
    public required string SourceUrl { get; init; }

    public string? Copyright { get; init; }

    public string? CopyrightUrl { get; init; }

    /// <summary>The license the data is published under ("ODbL-1.0"), when it says.</summary>
    public string? License { get; init; }

    /// <summary>The acknowledgement the providers ask for.</summary>
    public string? Acknowledgement { get; init; }

    /// <summary>The creator of the survey and of the HiPS version of it.</summary>
    public string? Creator { get; init; }

    /// <summary>The status line of the HiPS ("public master clonableOnce"); it says whether a full copy may be made.</summary>
    public string? Status { get; init; }

    public int TileWidth { get; init; } = 512;

    /// <summary>The finest order the survey has.</summary>
    public int MaxOrder { get; init; } = 9;

    /// <summary>The file type of the tiles: "jpeg" or "png".</summary>
    public string TileFormat { get; init; } = "jpeg";

    public string Frame { get; init; } = "equatorial";

    /// <summary>
    /// The survey declares that a full copy is allowed (its status says "clonable", not "unclonable"). A cache of the tiles that a person looked at is one thing,
    /// a mirror another: only this flag may ever enable the second, and nothing in V1 does.
    /// </summary>
    public bool AllowsFullMirror =>
        Status is { } status && status.Contains("clonable", StringComparison.OrdinalIgnoreCase) && !status.Contains("unclonable", StringComparison.OrdinalIgnoreCase);

    /// <summary>The short line for the workspace: "DSS colored · © Digitized Sky Survey - STScI/NASA, Colored &amp; Healpixed by CDS · License ODbL-1.0".</summary>
    public string AttributionLine => string.Join(
        " · ", new[] { Title ?? Id, Copyright is null ? null : "© " + Copyright, License is null ? null : "License " + License }.OfType<string>());
}

/// <summary>An image of the sky as pixels, in the tangent-plane view that was asked for: straight RGBA, row by row from the top.</summary>
/// <param name="TilesMissing">Tiles that were needed and could not be had (offline, a missing file, a timeout); their pixels are transparent.</param>
/// <param name="TilesFromCache">Of the tiles that were had, how many came from the disk cache.</param>
public sealed record SkyImage(
    int Width, int Height, byte[] Rgba, int TilesNeeded, int TilesMissing, int TilesFromCache, SkySurveyInfo? Info, string? Note)
{
    /// <summary>Every tile that the view needs was there.</summary>
    public bool IsComplete => TilesMissing == 0 && TilesNeeded > 0;

    public bool HasNoImagery => TilesNeeded == 0 || TilesMissing == TilesNeeded;
}

/// <summary>
/// A source of sky images for the framing workspace. It describes a place and a scale and returns pixels; it says nothing about how they are obtained, so a remote
/// HiPS survey, a cache, a local HiPS directory or the user's own plate-solved image can stand behind it. A tile or an image that cannot be had is a
/// missing part of the result, never an exception of its own: only cancelling the token throws.
/// </summary>
public interface ISkySurveyProvider
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>What the survey says about itself and its rights; from the cache when the network is not there; <c>null</c> when it is not known at all.</summary>
    Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>The image of the view: the tiles that it needs and nothing else.</summary>
    Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default);
}

/// <summary>A decoded tile: straight RGBA, row by row from the top.</summary>
public sealed record DecodedTile(int Width, int Height, byte[] Rgba);

/// <summary>Decodes the bytes of a tile (JPEG or PNG). Kept apart so that this project needs no image library; the application supplies the one it has.</summary>
public interface ITileDecoder
{
    /// <summary>The decoded tile, or <c>null</c> when the bytes are not an image.</summary>
    DecodedTile? Decode(byte[] bytes);
}

/// <summary>Where a survey is: a name and the address its HiPS is served from. A list of these is configuration, not code of the workspace.</summary>
public sealed record SkySurveyDescriptor(string Id, string Title, string BaseUrl);

public static class SkySurveys
{
    /// <summary>
    /// The surveys that Sidera offers to begin with: HiPS served by the CDS. Their rights are published in their <c>properties</c> and are shown in the workspace;
    /// they are not Sidera's, and nothing of them is bundled.
    /// </summary>
    public static IReadOnlyList<SkySurveyDescriptor> Defaults { get; } =
    [
        new("CDS/P/DSS2/color", "DSS2 color", "https://alasky.cds.unistra.fr/DSS/DSSColor"),
        new("CDS/P/DSS2/red", "DSS2 red", "https://alasky.cds.unistra.fr/DSS/DSS2Merged"),
        new("CDS/P/2MASS/color", "2MASS color (infrared)", "https://alasky.cds.unistra.fr/2MASS/Color"),
        new("CDS/P/PanSTARRS/DR1/color-z-zg-g", "Pan-STARRS DR1 color", "https://alasky.cds.unistra.fr/Pan-STARRS/DR1/color-z-zg-g"),
    ];

    public const string DefaultId = "CDS/P/DSS2/color";
}
