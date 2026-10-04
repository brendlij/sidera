using System.Text.Json;

namespace Sidera.Phd2;

/// <summary>
/// The names of the protocol of the PHD2 event server, as the documentation of PHD2 spells them (the wiki page "EventMonitoring").
/// Only what Sidera uses.
/// </summary>
internal static class Phd2Protocol
{
    public static class Methods
    {
        public const string GetAppState = "get_app_state";
        public const string GetConnected = "get_connected";
        public const string SetConnected = "set_connected";
        public const string GetCurrentEquipment = "get_current_equipment";
        public const string GetProfile = "get_profile";
        public const string GetProfiles = "get_profiles";
        public const string GetCalibrated = "get_calibrated";
        public const string GetExposure = "get_exposure";
        public const string GetPixelScale = "get_pixel_scale";
        public const string Guide = "guide";
        public const string StopCapture = "stop_capture";
        public const string Dither = "dither";
        public const string SetPaused = "set_paused";
    }

    public static class Events
    {
        public const string Version = "Version";
        public const string AppState = "AppState";
        public const string LockPositionSet = "LockPositionSet";
        public const string StarSelected = "StarSelected";
        public const string StartGuiding = "StartGuiding";
        public const string Paused = "Paused";
        public const string Resumed = "Resumed";
        public const string StartCalibration = "StartCalibration";
        public const string Calibrating = "Calibrating";
        public const string CalibrationComplete = "CalibrationComplete";
        public const string CalibrationFailed = "CalibrationFailed";
        public const string LoopingExposures = "LoopingExposures";
        public const string LoopingExposuresStopped = "LoopingExposuresStopped";
        public const string GuidingStopped = "GuidingStopped";
        public const string GuideStep = "GuideStep";
        public const string StarLost = "StarLost";
        public const string GuidingDithered = "GuidingDithered";
        public const string SettleBegin = "SettleBegin";
        public const string Settling = "Settling";
        public const string SettleDone = "SettleDone";
        public const string LockPositionLost = "LockPositionLost";
        public const string Alert = "Alert";
    }

    /// <summary>The values of the State of an <c>AppState</c> event and of the result of <c>get_app_state</c>.</summary>
    public static class AppStates
    {
        public const string Stopped = "Stopped";
        public const string Selected = "Selected";
        public const string Calibrating = "Calibrating";
        public const string Guiding = "Guiding";
        public const string LostLock = "LostLock";
        public const string Paused = "Paused";
        public const string Looping = "Looping";
    }
}

/// <summary>A notification of PHD2: its name and all of its attributes.</summary>
internal sealed record Phd2Event(string Name, JsonElement Body)
{
    public double? Number(string name) =>
        Body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public string? Text(string name) => Body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public bool? Flag(string name) => Body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>The answer to a request: a result, or an error with its code and message.</summary>
internal sealed record Phd2Response(long Id, JsonElement? Result, int? ErrorCode, string? ErrorMessage);

/// <summary>Reads one line of the stream of PHD2: a response to a request, a notification, or something that is neither.</summary>
internal static class Phd2Parser
{
    /// <returns>A <see cref="Phd2Response"/>, a <see cref="Phd2Event"/>, or <c>null</c> with <paramref name="problem"/> saying why.</returns>
    public static object? Parse(string line, out string? problem)
    {
        problem = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            problem = "not valid JSON: " + ex.Message;
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "not a JSON object";
                return null;
            }

            if (root.TryGetProperty("Event", out var name) && name.ValueKind == JsonValueKind.String)
            {
                return new Phd2Event(name.GetString()!, root.Clone());
            }

            if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number))
            {
                var hasError = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object;
                var hasResult = root.TryGetProperty("result", out var result);
                if (hasError)
                {
                    var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -1;
                    var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    return new Phd2Response(number, null, code, message ?? "no message");
                }

                if (hasResult)
                {
                    return new Phd2Response(number, result.Clone(), null, null);
                }
            }

            problem = "neither a notification nor an answer to a request";
            return null;
        }
    }
}
