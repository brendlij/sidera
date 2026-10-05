using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Sidera.Ascom;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Hardware;

/// <summary>The equipment file cannot be used. The message is a sentence for the user.</summary>
public sealed class EquipmentConfigurationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads and writes the equipment file: JSON, <c>"format": "astra-equipment"</c>, <c>"version": 1</c>. Reading is strict about
/// what Sidera needs (ids, names, kinds, backends, the ProgId of an ASCOM device, unique ids) and ignores properties it does
/// not know, so that a later version can add some. A file of a newer version is refused, not half read.
/// </summary>
public static class EquipmentConfigurationSerializer
{
    public static byte[] Serialize(EquipmentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("format", EquipmentConfiguration.FormatId);
            w.WriteNumber("version", EquipmentConfiguration.CurrentVersion);

            w.WriteStartArray("devices");
            foreach (var device in configuration.Devices)
            {
                w.WriteStartObject();
                w.WriteString("id", device.Id);
                w.WriteString("name", device.Name);
                w.WriteString("type", device.Type.ToString());
                w.WriteString("backend", DeviceBackends.Name(device.Backend));
                if (device.Settings.Count > 0)
                {
                    w.WriteStartObject("settings");
                    foreach (var (key, value) in device.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
                    {
                        w.WriteString(key, value);
                    }

                    w.WriteEndObject();
                }

                if (device.Preferences.Count > 0)
                {
                    w.WriteStartObject("preferences");
                    foreach (var (key, value) in device.Preferences.OrderBy(s => s.Key, StringComparer.Ordinal))
                    {
                        w.WriteString(key, value);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            if (configuration.Rigs.Count > 0)
            {
                w.WriteStartArray("rigs");
                foreach (var rig in configuration.Rigs)
                {
                    w.WriteStartObject();
                    w.WriteString("id", rig.Id);
                    w.WriteString("name", rig.Name);
                    w.WriteString("cameraId", rig.CameraId);
                    if (rig.FocuserId is not null)
                    {
                        w.WriteString("focuserId", rig.FocuserId);
                    }

                    if (rig.FilterWheelId is not null)
                    {
                        w.WriteString("filterWheelId", rig.FilterWheelId);
                    }

                    if (rig.RotatorId is not null)
                    {
                        w.WriteString("rotatorId", rig.RotatorId);
                        if (rig.RotatorModel is { } model)
                        {
                            // The calibration of the rotator: how its position relates to the rotation of the sky in the image. The solved rotation itself is never stored.
                            w.WriteStartObject("rotator");
                            w.WriteNumber("skyOffsetDegrees", model.OffsetDegrees);
                            w.WriteBoolean("reversed", model.Reversed);
                            if (model.CalibratedAt is { } at)
                            {
                                w.WriteString("calibratedAt", at.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                            }

                            w.WriteEndObject();
                        }
                    }

                    if (rig.Optics is { } optics)
                    {
                        // Only the inputs: the pixel scale, the sensor size and the field of view are derived and never stored.
                        w.WriteStartObject("optics");
                        w.WriteNumber("focalLengthMm", optics.FocalLengthMm);
                        WriteOptional(w, "apertureMm", optics.ApertureMm);
                        WriteOptional(w, "pixelSizeXMicrons", optics.PixelSizeXMicrons);
                        WriteOptional(w, "pixelSizeYMicrons", optics.PixelSizeYMicrons);
                        if (optics.SensorWidthPixels is { } sensorWidth)
                        {
                            w.WriteNumber("sensorWidthPixels", sensorWidth);
                        }

                        if (optics.SensorHeightPixels is { } sensorHeight)
                        {
                            w.WriteNumber("sensorHeightPixels", sensorHeight);
                        }

                        w.WriteEndObject();
                    }

                    if (rig.SimulatedBestFocus is { } best)
                    {
                        w.WriteNumber("simulatedBestFocus", best);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <exception cref="EquipmentConfigurationException">The content is not a usable equipment file.</exception>
    public static EquipmentConfiguration Deserialize(ReadOnlySpan<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new EquipmentConfigurationException("The equipment file is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "format") != EquipmentConfiguration.FormatId)
            {
                throw new EquipmentConfigurationException("This is not an Sidera equipment file.");
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1)
            {
                throw new EquipmentConfigurationException("The equipment file has no valid version.");
            }

            if (number > EquipmentConfiguration.CurrentVersion)
            {
                throw new EquipmentConfigurationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The equipment file is of version {number}; this version of Sidera reads up to version {EquipmentConfiguration.CurrentVersion}."));
            }

            var devices = new List<DeviceConfiguration>();
            if (root.TryGetProperty("devices", out var deviceArray))
            {
                RequireArray(deviceArray, "devices");
                foreach (var element in deviceArray.EnumerateArray())
                {
                    var device = ReadDevice(element);
                    if (devices.Any(d => string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new EquipmentConfigurationException($"The device id '{device.Id}' is used twice.");
                    }

                    devices.Add(device);
                }
            }

            var rigs = new List<RigConfiguration>();
            if (root.TryGetProperty("rigs", out var rigArray))
            {
                RequireArray(rigArray, "rigs");
                foreach (var element in rigArray.EnumerateArray())
                {
                    var rig = ReadRig(element, devices);
                    if (rigs.Any(r => string.Equals(r.Id, rig.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new EquipmentConfigurationException($"The rig id '{rig.Id}' is used twice.");
                    }

                    rigs.Add(rig);
                }
            }

            return new EquipmentConfiguration(devices, rigs);
        }
    }

    private static DeviceConfiguration ReadDevice(JsonElement element)
    {
        RequireObject(element, "a device");
        var id = Required(element, "id", "a device");
        if (EquipmentIds.Problem(id) is { } problem)
        {
            throw new EquipmentConfigurationException($"The device id '{id}' is not valid. {problem}");
        }

        var name = Required(element, "name", $"the device '{id}'");
        var typeName = Required(element, "type", $"the device '{id}'");
        if (!Enum.TryParse<DeviceType>(typeName, ignoreCase: false, out var type) || type is DeviceType.Weather)
        {
            throw new EquipmentConfigurationException($"The device '{id}' has the unknown type '{typeName}'.");
        }

        var backendName = Required(element, "backend", $"the device '{id}'");
        if (!DeviceBackends.TryParse(backendName, out var backend))
        {
            throw new EquipmentConfigurationException($"The device '{id}' has the unknown backend '{backendName}'.");
        }

        var settings = new Dictionary<string, string>();
        if (element.TryGetProperty("settings", out var settingsElement))
        {
            RequireObject(settingsElement, $"the settings of '{id}'");
            foreach (var property in settingsElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new EquipmentConfigurationException($"The setting '{property.Name}' of '{id}' must be text.");
                }

                settings[property.Name] = property.Value.GetString()!;
            }
        }

        var preferences = new Dictionary<string, string>();
        if (element.TryGetProperty("preferences", out var preferencesElement))
        {
            RequireObject(preferencesElement, $"the preferences of '{id}'");
            foreach (var property in preferencesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new EquipmentConfigurationException($"The preference '{property.Name}' of '{id}' must be text.");
                }

                preferences[property.Name] = property.Value.GetString()!;
            }
        }

        if (backend == DeviceBackend.Ascom)
        {
            if (!AscomDeviceFactory.Supports(type))
            {
                throw new EquipmentConfigurationException($"The device '{id}' is an ASCOM {type}, which Sidera does not support yet.");
            }

            if (!settings.TryGetValue(DeviceConfiguration.ProgIdKey, out var progId) || string.IsNullOrWhiteSpace(progId))
            {
                throw new EquipmentConfigurationException($"The ASCOM device '{id}' has no ProgId.");
            }
        }

        return new DeviceConfiguration(id, name, type, backend, settings) { Preferences = preferences };
    }

    private static RigConfiguration ReadRig(JsonElement element, List<DeviceConfiguration> devices)
    {
        RequireObject(element, "a rig");
        var id = Required(element, "id", "a rig");
        var name = Required(element, "name", $"the rig '{id}'");
        var cameraId = Required(element, "cameraId", $"the rig '{id}'");
        var focuserId = Optional(element, "focuserId");
        var wheelId = Optional(element, "filterWheelId");
        var rotatorId = Optional(element, "rotatorId");
        Sidera.Core.Rotators.RotatorSkyModel? rotatorModel = null;
        if (rotatorId is not null && element.TryGetProperty("rotator", out var rotator) && rotator.ValueKind == JsonValueKind.Object)
        {
            var offset = OptionalNumber(rotator, "skyOffsetDegrees");
            if (offset is { } skyOffset && double.IsFinite(skyOffset))
            {
                DateTimeOffset? calibratedAt = rotator.TryGetProperty("calibratedAt", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                rotatorModel = new Sidera.Core.Rotators.RotatorSkyModel(
                    Sidera.Core.Astrometry.SkyMath.NormalizeRotationDegrees(skyOffset),
                    rotator.TryGetProperty("reversed", out var reversed) && reversed.ValueKind == JsonValueKind.True, calibratedAt);
            }
        }


        // A rig of an older file may have no optics, or the older names (one pixel size, the resolution); both still load.
        OpticalTrain? train = null;
        if (element.TryGetProperty("optics", out var optics) && optics.ValueKind != JsonValueKind.Null)
        {
            RequireObject(optics, $"the optics of '{id}'");
            try
            {
                var legacyPixel = OptionalNumber(optics, "pixelSizeMicrons");
                train = new OpticalTrain(
                    Number(optics, "focalLengthMm", id),
                    OptionalNumber(optics, "apertureMm"),
                    OptionalNumber(optics, "pixelSizeXMicrons") ?? legacyPixel,
                    OptionalNumber(optics, "pixelSizeYMicrons") ?? legacyPixel,
                    OptionalCount(optics, "sensorWidthPixels") ?? OptionalCount(optics, "resolutionWidth"),
                    OptionalCount(optics, "sensorHeightPixels") ?? OptionalCount(optics, "resolutionHeight"));
            }
            catch (ArgumentException ex)
            {
                throw new EquipmentConfigurationException($"The optics of the rig '{id}' are not valid: {ex.Message.Split('\n', 2)[0]}", ex);
            }
        }

        int? best = element.TryGetProperty("simulatedBestFocus", out var bestElement) && bestElement.ValueKind == JsonValueKind.Number && bestElement.TryGetInt32(out var value) ? value : null;

        // The rig refers to devices by id; a rig whose device is not in the file would only fail later.
        foreach (var reference in new[] { cameraId, focuserId, wheelId, rotatorId }.OfType<string>())
        {
            if (!devices.Any(d => string.Equals(d.Id, reference, StringComparison.OrdinalIgnoreCase)))
            {
                throw new EquipmentConfigurationException($"The rig '{id}' refers to the device '{reference}', which is not in the file.");
            }
        }

        return new RigConfiguration(id, name, cameraId, focuserId, wheelId, train, best, rotatorId, rotatorModel);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Optional(JsonElement element, string name) =>
        String(element, name) is { Length: > 0 } text ? text : null;

    private static string Required(JsonElement element, string name, string owner) =>
        String(element, name) is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new EquipmentConfigurationException($"{Capitalize(owner)} has no '{name}'.");

    private static void WriteOptional(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
    }

    private static double? OptionalNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static int? OptionalCount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static double Number(JsonElement element, string name, string rig) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : throw new EquipmentConfigurationException($"The optics of the rig '{rig}' have no number '{name}'.");

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new EquipmentConfigurationException($"{Capitalize(what)} must be an object.");
        }
    }

    private static void RequireArray(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new EquipmentConfigurationException($"'{what}' must be a list.");
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The bytes as text, for tests and diagnostics.</summary>
    public static string ToText(EquipmentConfiguration configuration) => new UTF8Encoding(false).GetString(Serialize(configuration));
}
