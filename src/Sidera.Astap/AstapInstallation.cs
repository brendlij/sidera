using System.Text.RegularExpressions;
using Sidera.Core.Astrometry;

namespace Sidera.Astap;

/// <summary>What the locator and the detector need from the disk, so that an installation can be pretended in a test.</summary>
public interface IAstapFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>The names (not the paths) of the files directly in a folder; empty for a folder that is not there.</summary>
    IReadOnlyList<string> FileNames(string directory);

    /// <summary>The folders in which a program of the user is usually installed: Program Files, its 32-bit sibling, the programs of the user.</summary>
    IReadOnlyList<string> InstallRoots { get; }
}

public sealed class SystemAstapFileSystem : IAstapFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> FileNames(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f))] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> InstallRoots
    {
        get
        {
            var roots = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path))
                {
                    roots.Add(path);
                }
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
            {
                roots.Add(Path.Combine(local, "Programs"));
            }

            return [.. roots.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }
}

/// <summary>A star database of ASTAP that is installed: its name ("D50"), its folder and how many files it has.</summary>
public sealed record AstapDatabaseInfo(string Name, string Directory, int FileCount);

/// <summary>Where ASTAP is and whether it can solve: the program, and a star database that is really there.</summary>
public sealed record AstapInstallation(string? ExecutablePath, bool ExecutableWasConfigured, string? DatabaseDirectory, AstapDatabaseInfo? Database, string? ExecutableProblem)
{
    public bool HasExecutable => ExecutablePath is not null;

    public bool HasDatabase => Database is not null;

    public bool IsUsable => HasExecutable && HasDatabase;

    /// <summary>What a person reads: "ASTAP installed", "Star database: D50", or what is missing.</summary>
    public PlateSolverStatus ToStatus()
    {
        var lines = new List<string>();
        if (ExecutablePath is { } exe)
        {
            lines.Add(ExecutableWasConfigured ? $"ASTAP installed ({exe}, configured)" : $"ASTAP installed ({exe})");
        }
        else
        {
            lines.Add(ExecutableProblem ?? "ASTAP was not found");
        }

        string? problem = null;
        if (Database is { } db)
        {
            lines.Add($"Star database: {db.Name}");
        }
        else if (ExecutablePath is not null)
        {
            lines.Add(DatabaseDirectory is { } dir ? $"No usable star database found in {dir}" : "No usable star database found");
            problem = "ASTAP needs a star database (for example D50). Install one from the ASTAP download page; Sidera does not install it.";
        }

        problem ??= ExecutablePath is null ? ExecutableProblem ?? "ASTAP was not found. Install it, or set its path." : null;
        return new PlateSolverStatus(IsUsable, lines, problem);
    }
}

/// <summary>Finds the ASTAP program and its star database. It looks, and changes nothing: no download, no change of the path.</summary>
public static partial class AstapLocator
{
    public const string ConsoleExecutable = "astap_cli.exe";
    public const string WindowExecutable = "astap.exe";

    // The order the databases are preferred in when several are installed and none is chosen: the larger and deeper first. A choice, not a requirement.
    private static readonly string[] Preference = ["D80", "D50", "D20", "D05", "V50", "G18", "G17", "G05", "H18", "H17"];

    // A database is a set of files named like d50_0101.1476 (or g18_0101.290): the letter and the two digits are its name.
    [GeneratedRegex(@"^(?<name>[a-z][0-9]{2})_[0-9]{4}\.(1476|290)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseFile();

    public static AstapInstallation Locate(AstapConfiguration configuration, IAstapFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(fs);

        string? executable = null;
        string? problem = null;
        var configured = !string.IsNullOrWhiteSpace(configuration.ExecutablePath);
        if (configured)
        {
            var path = configuration.ExecutablePath!.Trim();
            if (fs.FileExists(path))
            {
                executable = PreferConsole(path, fs);
            }
            else
            {
                problem = $"The configured ASTAP program was not found: {path}";
            }
        }
        else
        {
            foreach (var root in fs.InstallRoots)
            {
                var folder = Path.Combine(root, "astap");
                foreach (var name in new[] { ConsoleExecutable, WindowExecutable })
                {
                    var candidate = Path.Combine(folder, name);
                    if (fs.FileExists(candidate))
                    {
                        executable = candidate;
                        break;
                    }
                }

                if (executable is not null)
                {
                    break;
                }
            }

            problem = executable is null ? "ASTAP was not found in the usual places. Install it, or set its path." : null;
        }

        var databaseDirectory = !string.IsNullOrWhiteSpace(configuration.DatabasePath)
            ? configuration.DatabasePath!.Trim()
            : executable is null ? null : Path.GetDirectoryName(executable);
        var database = databaseDirectory is null ? null : DetectDatabase(databaseDirectory, configuration.DatabaseAbbreviation, fs);
        return new AstapInstallation(executable, configured, databaseDirectory, database, problem);
    }

    // astap.exe given: the console variant beside it does the same without a window.
    private static string PreferConsole(string path, IAstapFileSystem fs)
    {
        if (!string.Equals(Path.GetFileName(path), WindowExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var sibling = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, ConsoleExecutable);
        return fs.FileExists(sibling) ? sibling : path;
    }

    /// <summary>The star database in a folder, or <c>null</c> when there is none: files that look like the tiles of a database, not a guess from the program.</summary>
    public static AstapDatabaseInfo? DetectDatabase(string directory, string? preferred, IAstapFileSystem fs)
    {
        var groups = fs.FileNames(directory)
            .Select(n => DatabaseFile().Match(n))
            .Where(m => m.Success)
            .GroupBy(m => m.Groups["name"].Value.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Count());
        if (groups.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var wanted = preferred.Trim().ToUpperInvariant();
            return groups.TryGetValue(wanted, out var count) ? new AstapDatabaseInfo(wanted, directory, count) : null;
        }

        var name = Preference.FirstOrDefault(groups.ContainsKey) ?? groups.Keys.Order(StringComparer.Ordinal).First();
        return new AstapDatabaseInfo(name, directory, groups[name]);
    }
}
