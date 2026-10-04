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
    public const string Offset = "camera.offset";
    public const string BinX = "camera.binX";
    public const string BinY = "camera.binY";
    public const string ReadoutMode = "camera.readoutMode";
    public const string FastReadout = "camera.fastReadout";
    public const string TargetTemperature = "camera.targetTemperature";
    public const string TrackingRate = "mount.trackingRate";
    public const string GuideRateRa = "mount.guideRateRa";
    public const string GuideRateDec = "mount.guideRateDec";
    public const string Refraction = "mount.refraction";
    public const string TempComp = "focuser.tempComp";

    /// <summary>The camera preferences of a set of settings: what was set on the camera, without the subframe and the cooler.</summary>
    public static IReadOnlyDictionary<string, string> From(CameraSettings settings, IReadOnlyDictionary<string, string> existing)
    {
        var result = new Dictionary<string, string>(existing);
        Put(result, Gain, settings.Gain);
        Put(result, Offset, settings.Offset);
        Put(result, BinX, settings.BinX);
        Put(result, BinY, settings.BinY);
        Put(result, ReadoutMode, settings.ReadoutMode);
        if (settings.FastReadout is { } fast)
        {
            result[FastReadout] = fast ? "true" : "false";
        }

        if (settings.TargetTemperature is { } target)
        {
            result[TargetTemperature] = target.ToString("R", CultureInfo.InvariantCulture);
        }

        return result;
    }

    /// <summary>The change that applies the camera preferences; empty when there are none.</summary>
    public static CameraSettings ToCameraChange(IReadOnlyDictionary<string, string> preferences) => new()
    {
        Gain = Int(preferences, Gain),
        Offset = Int(preferences, Offset),
        BinX = Int(preferences, BinX),
        BinY = Int(preferences, BinY),
        ReadoutMode = Int(preferences, ReadoutMode),
        FastReadout = Bool(preferences, FastReadout),
        TargetTemperature = Double(preferences, TargetTemperature),
    };

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
