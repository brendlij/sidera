using System.Globalization;
using Sidera.Core.Framing;

namespace Sidera.Desktop.ViewModels;

/// <summary>The words drawn on the frame of the framing workspace; kept here so that they are one thing and can be tested.</summary>
public static class FramingLabels
{
    /// <summary>The size of the field on the frame: "1.79° × 1.20°"; empty while the rig has no known field.</summary>
    public static string Field(RigField? field) => field is { } f
        ? string.Create(CultureInfo.InvariantCulture, $"{f.WidthDegrees:0.00}° × {f.HeightDegrees:0.00}°")
        : string.Empty;

    /// <summary>The mark of the top of the frame with the desired rotation: "top 87.5°".</summary>
    public static string Top(double rotationDegrees) => string.Create(CultureInfo.InvariantCulture, $"top {rotationDegrees:0.#}°");

    /// <summary>
    /// The picture's brightness slider (0 to 1) as a gamma: 1 leaves the survey as it is, a larger one lifts the dark parts of the sky, which is what a person who cannot see
    /// the nebula wants. A gamma and not a gain, so that stars do not clip.
    /// </summary>
    public static double GammaOf(double brightness) => 1.0 + 2.5 * System.Math.Clamp(brightness, 0, 1);

    /// <summary>The 256 values of the brightness: what each shade of the survey becomes.</summary>
    public static byte[] BrightnessTable(double brightness)
    {
        var table = new byte[256];
        var inverse = 1.0 / GammaOf(brightness);
        for (var i = 0; i < 256; i++)
        {
            table[i] = (byte)System.Math.Round(255.0 * System.Math.Pow(i / 255.0, inverse));
        }

        return table;
    }
}
