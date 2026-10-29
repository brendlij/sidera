using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sidera.Core.Devices;

namespace Sidera.Core.Imaging;

/// <summary>What is known about a frame when its file is named: only what is set is used, a part that is not known is left out of the name.</summary>
/// <param name="Start">When the exposure started, in local time.</param>
public sealed record FrameNameValues(
    string? Target,
    string? Setup,
    string? Camera,
    string? Filter,
    FrameType Type,
    double ExposureSeconds,
    DateTime Start,
    int? Gain = null,
    int? Offset = null,
    double? TemperatureCelsius = null,
    int BinX = 1,
    int BinY = 1);

/// <summary>One part that a name pattern can use, as it is written (<c>{target}</c>) and what it stands for.</summary>
public sealed record FrameNamePart(string Token, string Meaning, string Example);

/// <summary>
/// The names of the files that Sidera saves: a pattern for the folders and one for the file, made of parts in braces (<c>{target}</c>, <c>{filter}</c>, <c>{n}</c> …) and of text. Folders in a pattern are
/// separated by <c>/</c> (or <c>\</c>). A part whose value is not known is left out, together with the separator next to it, and whatever a file system does not accept in a name is replaced; a pattern never
/// makes a path that leaves its folder.
/// </summary>
public static class FrameNaming
{
    public const string DefaultFolderPattern = "{target}/{night}/{type}";
    public const string DefaultFilePattern = "{target}_{type}_{filter}_{exposure}_{n}";

    public static IReadOnlyList<FrameNamePart> Parts { get; } =
    [
        new("target", "The name of the target of the session", "M31"),
        new("setup", "The imaging setup", "Newton"),
        new("camera", "The camera", "CameraA"),
        new("filter", "The filter in the light path", "Ha"),
        new("type", "Light, Dark, Flat or Bias", "Light"),
        new("exposure", "The exposure time in seconds", "300s"),
        new("night", "The date of the evening that the night began (a night that goes past midnight stays one)", "2026-10-12"),
        new("date", "The date of the exposure", "2026-10-13"),
        new("time", "The time of the exposure start", "014530"),
        new("n", "A number that counts up in the folder (4 digits)", "0007"),
        new("gain", "The gain of the camera", "gain100"),
        new("offset", "The offset of the camera", "offset50"),
        new("temp", "The temperature of the sensor in degrees Celsius", "-10C"),
        new("bin", "The binning", "bin1x1"),
    ];

    private static readonly Regex Token = new(@"\{([^{}]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Known = new(Parts.Select(p => p.Token), StringComparer.OrdinalIgnoreCase);

    /// <summary>What is wrong with a pattern, in a sentence; <c>null</c> when it can be used.</summary>
    public static string? Problem(string? pattern, bool isFile)
    {
        var label = isFile ? "The file name" : "The folder pattern";
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return isFile ? "The file name needs a pattern." : null;
        }

        foreach (Match match in Token.Matches(pattern))
        {
            if (!Known.Contains(match.Groups[1].Value.Trim()))
            {
                return $"{label} has an unknown part {{{match.Groups[1].Value}}}. Use: {string.Join(", ", Parts.Select(p => "{" + p.Token + "}"))}.";
            }
        }

        var rest = Token.Replace(pattern, string.Empty);
        if (rest.Contains('{', StringComparison.Ordinal) || rest.Contains('}', StringComparison.Ordinal))
        {
            return $"{label} has a brace that does not belong to a part.";
        }

        if (isFile && (pattern.Contains('/', StringComparison.Ordinal) || pattern.Contains('\\', StringComparison.Ordinal)))
        {
            return "The file name cannot hold a folder: use the folder pattern for that.";
        }

        if (!isFile && Path.IsPathRooted(pattern.Replace('/', Path.DirectorySeparatorChar)))
        {
            return "The folder pattern is inside the save folder: it cannot start with a drive or a slash.";
        }

        return null;
    }

    /// <summary>The folders of the pattern for a frame, relative to the save folder, as path segments.</summary>
    public static IReadOnlyList<string> Folders(string? pattern, FrameNameValues values) =>
        string.IsNullOrWhiteSpace(pattern)
            ? []
            : Resolve(pattern, values, number: 0).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Clean).Where(s => s.Length > 0).ToList();

    /// <summary>The name of the file for a frame without its extension; <paramref name="number"/> replaces <c>{n}</c>.</summary>
    public static string FileName(string pattern, FrameNameValues values, int number)
    {
        var name = Clean(Resolve(pattern.Replace('\\', '/').Replace("/", "_", StringComparison.Ordinal), values, number));
        return name.Length > 0 ? name : "frame_" + number.ToString("0000", CultureInfo.InvariantCulture);
    }

    // The pattern with its parts replaced; folder separators are kept so that they can split it afterwards.
    private static string Resolve(string pattern, FrameNameValues v, int number)
    {
        var text = pattern.Replace('\\', '/');
        return Token.Replace(text, match => Value(match.Groups[1].Value.Trim().ToLowerInvariant(), v, number));
    }

    private static string Value(string part, FrameNameValues v, int number)
    {
        var inv = CultureInfo.InvariantCulture;
        return part switch
        {
            "target" => Segment(v.Target),
            "setup" => Segment(v.Setup),
            "camera" => Segment(v.Camera),
            "filter" => Segment(v.Filter),
            "type" => v.Type.ToString(),
            "exposure" => v.ExposureSeconds > 0 ? v.ExposureSeconds.ToString("0.###", inv) + "s" : string.Empty,
            "night" => v.Start.AddHours(-12).ToString("yyyy-MM-dd", inv),
            "date" => v.Start.ToString("yyyy-MM-dd", inv),
            "time" => v.Start.ToString("HHmmss", inv),
            "n" => number > 0 ? number.ToString("0000", inv) : string.Empty,
            "gain" => v.Gain is { } g ? "gain" + g.ToString(inv) : string.Empty,
            "offset" => v.Offset is { } o ? "offset" + o.ToString(inv) : string.Empty,
            "temp" => v.TemperatureCelsius is { } t && double.IsFinite(t) ? Math.Round(t).ToString("0", inv) + "C" : string.Empty,
            "bin" => $"bin{v.BinX.ToString(inv)}x{v.BinY.ToString(inv)}",
            _ => string.Empty,
        };
    }

    // A value never holds a separator of its own: a target called "NGC 7000/North America" stays in one folder.
    private static string Segment(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace('/', '-').Replace('\\', '-');

    /// <summary>A name that every file system takes: what it does not accept is replaced, runs of separators are one, and a part that was left out leaves no gap.</summary>
    public static string Clean(string name)
    {
        var builder = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in name.Trim())
        {
            builder.Append(c == ' ' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var text = Regex.Replace(builder.ToString(), "_{2,}", "_").Trim('_', '-', '.');
        return text.Length > 120 ? text[..120].TrimEnd('_', '-', '.') : text;
    }
}
