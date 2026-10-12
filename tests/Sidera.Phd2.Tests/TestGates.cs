using Sidera.Core;

namespace Sidera.Phd2.Tests;

/// <summary>A gate of the tests of a real PHD2 is open unless it is set to 0, false, no or off. What decides whether they run is whether a PHD2 answers.</summary>
internal static class TestGates
{
    public static bool Allowed(string name) => SideraEnvironment.Get(name)?.Trim().ToLowerInvariant() is not ("0" or "false" or "no" or "off");
}
