using Sidera.Core;
namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// A test that needs the installed ASCOM Platform and its simulators. It runs when the Platform is installed on this computer, and not when
/// <c>SIDERA_ASCOM_TESTS=0</c> is set.
/// </summary>
public sealed class AscomFactAttribute : FactAttribute
{
    public const string Variable = "SIDERA_ASCOM_TESTS";

    private static readonly bool PlatformInstalled = IsPlatformInstalled();

    private static bool IsPlatformInstalled()
    {
        try
        {
            return ASCOM.Com.PlatformUtilities.IsPlatformInstalled();
        }
        catch
        {
            return false;
        }
    }

    public AscomFactAttribute()
    {
        if (!TestGates.Allowed(Variable))
        {
            Skip = $"{Variable}=0.";
        }
        else if (!PlatformInstalled)
        {
            Skip = "Needs the ASCOM Platform and its simulators; it is not installed on this computer.";
        }
    }
}

/// <summary>
/// A test against real hardware. It runs only when the variable that names the device is set, for example
/// <c>SIDERA_ASCOM_FOCUSER=ASCOM.EAF.Focuser</c>; that is the only thing to set. Each test says what it does to the device and keeps it small and
/// reversible; a part of it that moves something is only left out when its own variable (<c>…_OK</c>) is set to 0.
/// </summary>
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(SideraEnvironment.Get(variable)))
        {
            Skip = $"Needs real hardware. Set {variable} to the ProgId of the device to run it.";
        }
    }
}

/// <summary>A test that opens a dialog and waits for a person. Runs only with <c>SIDERA_ASCOM_MANUAL=1</c>.</summary>
public sealed class ManualFactAttribute : FactAttribute
{
    public ManualFactAttribute()
    {
        if (SideraEnvironment.Get("SIDERA_ASCOM_MANUAL") != "1")
        {
            Skip = "Needs a person at the screen. Set SIDERA_ASCOM_MANUAL=1 to run it.";
        }
    }
}
