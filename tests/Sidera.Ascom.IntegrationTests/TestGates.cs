using Sidera.Core;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// The gates of the tests of real equipment (<c>SIDERA_ASCOM_MOUNT_SLEW_OK</c>, <c>SIDERA_MERIDIAN_FLIP_OK</c> and so on). A gate is open unless it is set to 0, false, no or off: what is tested
/// is chosen by naming the device (<c>SIDERA_ASCOM_MOUNT=&lt;ProgId&gt;</c>), not by a second variable. No gate belongs to the application.
/// </summary>
internal static class TestGates
{
    public static bool Allowed(string name) => SideraEnvironment.Get(name)?.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");
}
