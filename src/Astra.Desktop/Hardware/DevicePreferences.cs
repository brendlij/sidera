using System;
using System.Collections.Generic;
using System.Globalization;
using Astra.Core.Devices;
using Astra.Core.Mounts;

namespace Astra.Desktop.Hardware;

/// <summary>Where the preferences of a device are kept (the equipment file) and read back from. Implemented by the equipment service.</summary>
public interface IDevicePreferenceStore
{
    IReadOnlyDictionary<string, string> GetPreferences(string deviceId);

    /// <summary>Stores the preferences of the device; a problem is returned as a sentence, <c>null</c> means it was saved.</summary>
    string? SavePreferences(string deviceId, IReadOnlyDictionary<string, string> preferences);
}

/// <summary>
/// The preferences of a device as they are stored (text by key) and what they mean: what the user wanted last time, applied
/// after a connect to whatever the device supports. They are neither capabilities nor state. The cooler is deliberately not a
/// preference: Astra never switches a cooler on by itself.
/// </summary>
public static class DevicePreferences
{
    public const string Gain = "camera.gain";
    public const string GainName = "camera.gainName";
    public const string Offset = "camera.offset";
    public const string OffsetName = "camera.offsetName";
    public const string BinX = "camera.binX";
    public const string BinY = "camera.binY";

    /// <summary>"full" for the whole sensor, or "x,y,width,height" in binned pixels.</summary>
    public const string Region = "camera.region";

    /// <summary>The readout mode by its name: the list of modes can change, the name is what stays meaningful.</summary>
    public const string ReadoutMode = "camera.readoutMode";

    public const string FastReadout = "camera.fastReadout";
    public const string TargetTemperature = "camera.targetTemperature";
    public const string TrackingRate = "mount.trackingRate";
    public const string GuideRateRa = "mount.guideRateRa";
    public const string GuideRateDec = "mount.guideRateDec";
    public const string Refraction = "mount.refraction";
    public const string TempComp = "focuser.tempComp";

    /// <summary>
    /// The acquisition defaults of a camera: the settings it normally takes frames with, which an exposure inherits for everything it does not
    /// set itself. A gain or offset is a number, or the name of a choice for a camera whose gain is a list; the region and the readout mode
    /// are kept in terms that stay valid when the camera changes (the whole sensor, a mode by its name).
    /// </summary>
    public static AcquisitionIntent AcquisitionDefaults(IReadOnlyDictionary<string, string> preferences)
    {
        AcquisitionRegion? region = null;
        if (preferences.TryGetValue(Region, out var text))
        {
            if (text == "full")
            {
                region = AcquisitionRegion.Full;
            }
            else
            {
                var parts = text.Split(',');
                if (parts.Length == 4
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                    && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                    && x >= 0 && y >= 0 && w > 0 && h > 0)
                {
                    region = AcquisitionRegion.Of(x, y, w, h);
                }
            }
        }

        return new AcquisitionIntent
        {
            Gain = Level(preferences, Gain, GainName),
            Offset = Level(preferences, Offset, OffsetName),
            BinX = Int(preferences, BinX),
            BinY = Int(preferences, BinY),
            Region = region,
            ReadoutMode = preferences.TryGetValue(ReadoutMode, out var mode) && !string.IsNullOrWhiteSpace(mode) ? mode : null,
            FastReadout = Bool(preferences, FastReadout),
        };
    }

    /// <summary>The preferences with the settings that <paramref name="intent"/> sets replaced; what it does not set stays as it was.</summary>
    public static IReadOnlyDictionary<string, string> WithAcquisition(IReadOnlyDictionary<string, string> existing, AcquisitionIntent intent)
    {
        var result = new Dictionary<string, string>(existing);
        if (intent.Gain is { } gain)
        {
            PutLevel(result, Gain, GainName, gain);
        }

        if (intent.Offset is { } offset)
        {
            PutLevel(result, Offset, OffsetName, offset);
        }

        Put(result, BinX, intent.BinX);
        Put(result, BinY, intent.BinY);
        if (intent.Region is { } region)
        {
            result[Region] = region.IsFullFrame
                ? "full"
                : string.Create(CultureInfo.InvariantCulture, $"{region.X},{region.Y},{region.Width},{region.Height}");
        }

        if (intent.ReadoutMode is { } readout)
        {
            result[ReadoutMode] = readout;
        }

        if (intent.FastReadout is { } fast)
        {
            result[FastReadout] = fast ? "true" : "false";
        }

        return result;
    }

    public static double? TargetTemperatureOf(IReadOnlyDictionary<string, string> preferences) => Double(preferences, TargetTemperature);

    public static IReadOnlyDictionary<string, string> WithTargetTemperature(IReadOnlyDictionary<string, string> existing, double celsius) =>
        new Dictionary<string, string>(existing) { [TargetTemperature] = celsius.ToString("R", CultureInfo.InvariantCulture) };

    private static AcquisitionLevel? Level(IReadOnlyDictionary<string, string> preferences, string numberKey, string nameKey)
    {
        if (Int(preferences, numberKey) is { } number)
        {
            return AcquisitionLevel.OfNumber(number);
        }

        return preferences.TryGetValue(nameKey, out var name) && !string.IsNullOrWhiteSpace(name) ? AcquisitionLevel.OfName(name) : null;
    }

    private static void PutLevel(Dictionary<string, string> target, string numberKey, string nameKey, AcquisitionLevel level)
    {
        target.Remove(numberKey);
        target.Remove(nameKey);
        if (level.Name is { } name)
        {
            target[nameKey] = name;
        }
        else if (level.Number is { } number)
        {
            target[numberKey] = number.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static IReadOnlyDictionary<string, string> From(TrackingRate? rate, GuideRates? guideRates, bool? refraction, IReadOnlyDictionary<string, string> existing)
    {
        var result = new Dictionary<string, string>(existing);
        if (rate is { } r)
        {
            result[TrackingRate] = r.ToString();
        }

        if (guideRates is { } g)
        {
            result[GuideRateRa] = g.RightAscensionDegreesPerSecond.ToString("R", CultureInfo.InvariantCulture);
            result[GuideRateDec] = g.DeclinationDegreesPerSecond.ToString("R", CultureInfo.InvariantCulture);
        }

        if (refraction is { } f)
        {
            result[Refraction] = f ? "true" : "false";
        }

        return result;
    }

    public static TrackingRate? TrackingRateOf(IReadOnlyDictionary<string, string> preferences) =>
        preferences.TryGetValue(TrackingRate, out var text) && Enum.TryParse<TrackingRate>(text, out var rate) ? rate : null;

    public static GuideRates? GuideRatesOf(IReadOnlyDictionary<string, string> preferences) =>
        Double(preferences, GuideRateRa) is { } ra && Double(preferences, GuideRateDec) is { } dec && ra > 0 && dec > 0 ? new GuideRates(ra, dec) : null;

    public static bool? RefractionOf(IReadOnlyDictionary<string, string> preferences) => Bool(preferences, Refraction);

    public static bool? TempCompOf(IReadOnlyDictionary<string, string> preferences) => Bool(preferences, TempComp);

    public static IReadOnlyDictionary<string, string> WithTempComp(bool enabled, IReadOnlyDictionary<string, string> existing) =>
        new Dictionary<string, string>(existing) { [TempComp] = enabled ? "true" : "false" };

    private static void Put(Dictionary<string, string> target, string key, int? value)
    {
        if (value is { } v)
        {
            target[key] = v.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static int? Int(IReadOnlyDictionary<string, string> preferences, string key) =>
        preferences.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Double(IReadOnlyDictionary<string, string> preferences, string key) =>
        preferences.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static bool? Bool(IReadOnlyDictionary<string, string> preferences, string key) =>
        preferences.TryGetValue(key, out var text) && bool.TryParse(text, out var v) ? v : null;
}
