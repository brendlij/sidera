using Sidera.Core;

namespace Sidera.Runtime.Tests;

/// <summary>The variables of Sidera: SIDERA_ wins, the ASTRA_ name of the same variable is the fallback, an empty one is not set.</summary>
public sealed class SideraEnvironmentTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void ASideraVariable_IsRead()
    {
        Assert.Equal("a", SideraEnvironment.Get("SIDERA_ASCOM_CAMERA", Env(("SIDERA_ASCOM_CAMERA", "a"))));
    }

    [Fact]
    public void TheAstraVariableOfTheSameName_IsTheFallback()
    {
        Assert.Equal("legacy", SideraEnvironment.Get("SIDERA_EQUIPMENT_FILE", Env(("ASTRA_EQUIPMENT_FILE", "legacy"))));
    }

    [Fact]
    public void WhenBothAreSet_SideraWins()
    {
        var env = Env(("SIDERA_ASCOM_MOUNT", "new"), ("ASTRA_ASCOM_MOUNT", "old"));

        Assert.Equal("new", SideraEnvironment.Get("SIDERA_ASCOM_MOUNT", env));
    }

    [Fact]
    public void WhenNeitherIsSet_ThereIsNoValue()
    {
        Assert.Null(SideraEnvironment.Get("SIDERA_ASCOM_TESTS", Env()));
    }

    [Fact]
    public void AnEmptySideraVariable_IsNotSet_SoTheFallbackApplies()
    {
        var env = Env(("SIDERA_ASCOM_TESTS", ""), ("ASTRA_ASCOM_TESTS", "1"));

        Assert.Equal("1", SideraEnvironment.Get("SIDERA_ASCOM_TESTS", env));
    }

    [Fact]
    public void TheGatesOfPhysicalTests_DoNotOpenBecauseOfAVariableOfAnotherName()
    {
        // Only the same name with the old prefix is a fallback; a gate does not open for any other variable.
        var env = Env(("ASTRA_ASCOM_MOUNT_TRACKING_OK", "1"));

        Assert.Null(SideraEnvironment.Get("SIDERA_ASCOM_MOUNT_SLEW_OK", env));
        Assert.Equal("1", SideraEnvironment.Get("SIDERA_ASCOM_MOUNT_TRACKING_OK", env));
    }

    [Fact]
    public void AVariableThatIsNotOfSidera_IsReadAsItIs()
    {
        Assert.Equal("x", SideraEnvironment.Get("OTHER_VARIABLE", Env(("OTHER_VARIABLE", "x"))));
        Assert.Null(SideraEnvironment.Get("OTHER_VARIABLE", Env(("ASTRA_VARIABLE", "x"))));
    }
}
