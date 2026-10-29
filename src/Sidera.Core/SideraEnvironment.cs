namespace Sidera.Core;

/// <summary>
/// Environment variables of Sidera. The names start with <c>SIDERA_</c>. An empty value counts as not set.
/// </summary>
public static class SideraEnvironment
{
    public const string Prefix = "SIDERA_";

    /// <summary>The value of the variable <paramref name="name"/> (for example <c>SIDERA_EQUIPMENT_FILE</c>), or <c>null</c>.</summary>
    public static string? Get(string name) => Get(name, Environment.GetEnvironmentVariable);

    /// <summary>The same, reading the variables with <paramref name="read"/>: for tests.</summary>
    public static string? Get(string name, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var value = read(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
