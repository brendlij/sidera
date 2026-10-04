using Sidera.Core;
namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// A test that needs the installed ASCOM Platform and its simulators. It runs only when
/// <c>SIDERA_ASCOM_TESTS=1</c> is set, so that the normal run of the tests needs neither the platform nor a Windows
/// desktop session with drivers.
/// </summary>
public sealed class AscomFactAttribute : FactAttribute
{
    public const string Variable = "SIDERA_ASCOM_TESTS";

    public AscomFactAttribute()
    {
        if (SideraEnvironment.Get(Variable) != "1")
        {
            Skip = $"Needs the ASCOM Platform and its simulators. Set {Variable}=1 to run it.";
        }
    }
}

/// <summary>
/// A test against real hardware. It runs only when the variable that names the device is set, for example
/// <c>SIDERA_ASCOM_FOCUSER=ASCOM.EAF.Focuser</c>. Each test says what it does to the device and keeps it small and
/// reversible; none of them is run by accident.
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
