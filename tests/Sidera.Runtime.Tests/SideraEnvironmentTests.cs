using Sidera.Core;

namespace Sidera.Runtime.Tests;

/// <summary>The variables of Sidera: a SIDERA_ variable is read, an empty one is not set, other names are not read.</summary>
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
    public void AnEmptyVariable_IsNotSet()
    {
        Assert.Null(SideraEnvironment.Get("SIDERA_ASCOM_TESTS", Env(("SIDERA_ASCOM_TESTS", ""))));
    }

    [Fact]
    public void AMissingVariable_IsNotSet()
    {
        Assert.Null(SideraEnvironment.Get("SIDERA_ASCOM_MOUNT", Env()));
    }
}
