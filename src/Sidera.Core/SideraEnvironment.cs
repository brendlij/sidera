namespace Sidera.Core;

/// <summary>
/// Environment variables of Sidera. The names start with <c>SIDERA_</c>. The names of the product's earlier name, <c>ASTRA_</c>, still
/// work as a fallback, so that an environment that was set up before the rename keeps working: the <c>SIDERA_</c> variable wins when it
/// is set, otherwise the <c>ASTRA_</c> variable of the same name is used, otherwise there is no value. An empty value counts as not set.
/// </summary>
public static class SideraEnvironment
{
    public const string Prefix = "SIDERA_";

    /// <summary>The prefix of the names before the rename; only read, never written.</summary>
    public const string LegacyPrefix = "ASTRA_";

    /// <summary>The value of the variable <paramref name="name"/> (for example <c>SIDERA_EQUIPMENT_FILE</c>), or <c>null</c>.</summary>
    public static string? Get(string name) => Get(name, Environment.GetEnvironmentVariable);

    /// <summary>The same, reading the variables with <paramref name="read"/>: for tests.</summary>
    public static string? Get(string name, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var value = read(name);
        if (!string.IsNullOrEmpty(value) || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }

        var legacy = read(LegacyPrefix + name[Prefix.Length..]);
        return string.IsNullOrEmpty(legacy) ? null : legacy;
    }
}
