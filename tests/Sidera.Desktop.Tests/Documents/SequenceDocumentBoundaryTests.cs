using Sidera.Desktop.Documents;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// The persistence boundary by construction: the JSON of version 1 is known to the serializer only, and the user never
/// sees it. The project has no architecture-test tooling, so these read the source files of the Desktop project.
/// </summary>
public class SequenceDocumentBoundaryTests
{
    private static string DesktopSources()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Sidera.Desktop");
            if (File.Exists(Path.Combine(directory.FullName, "Sidera.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The source of Sidera.Desktop was not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Files(params string[] patterns)
    {
        var root = DesktopSources();
        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool InDocumentsFolder(string path) =>
        Path.GetRelativePath(DesktopSources(), path).StartsWith("Documents" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // The equipment file and the settings file are other files with a format of their own, kept in the Hardware and the Settings
    // folder: they may be JSON, and the sequence file still may not be known to be.
    private static bool InHardwareFolder(string path) =>
        Path.GetRelativePath(DesktopSources(), path).StartsWith("Hardware" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || Path.GetRelativePath(DesktopSources(), path).StartsWith("Settings" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    [Fact]
    public void OnlyTheSerializers_UseSystemTextJson_OneForTheSequenceTheEquipmentAndTheSettings()
    {
        var users = Files("*.cs")
            .Where(path => File.ReadAllText(path).Contains("System.Text.Json", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .Order()
            .ToList();

        Assert.Equal(["EquipmentConfigurationSerializer.cs", "JsonSequenceDocumentSerializer.cs", "SideraSettings.cs"], users);
    }

    [Fact]
    public void ViewModelsAndViews_DoNotKnowThatTheFormatIsJson()
    {
        var offenders = Files("*.cs", "*.axaml")
            .Where(path => !InDocumentsFolder(path) && !InHardwareFolder(path))
            .Where(path => File.ReadAllText(path).Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(DesktopSources(), path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NothingTheUserCanSee_MentionsJson_OrAJsonFileName()
    {
        // The windows, the view models and the file dialog: every string a person could read.
        var offenders = Files("*.axaml", "*.cs")
            .Where(path => !InDocumentsFolder(path) && !InHardwareFolder(path))
            .Where(path => File.ReadAllText(path).Contains(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
        Assert.DoesNotContain("json", SequenceDocumentFiles.FilterName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("json", SequenceDocumentFiles.DefaultFileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithinTheDocumentsFolder_OnlyTheSerializerAndTheDefaultStoreNameTheJsonImplementation()
    {
        var users = Files("*.cs")
            .Where(InDocumentsFolder)
            .Where(path => File.ReadAllText(path).Contains("JsonSequenceDocumentSerializer", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .Order()
            .ToList();

        Assert.Equal(["JsonSequenceDocumentSerializer.cs", "SequenceDocumentStore.cs"], users);
    }

    [Fact]
    public void TheDocumentModelAndTheSerializerContract_AreIndependentOfJson()
    {
        // The model and the interface are what the rest of Sidera works with; they mention no encoding.
        foreach (var name in new[] { "SequenceDocument.cs", "ISequenceDocumentSerializer.cs", "SequenceDocumentMapper.cs", "ISequenceFilePicker.cs" })
        {
            var text = File.ReadAllText(Path.Combine(DesktopSources(), "Documents", name));
            Assert.DoesNotContain("json", text.Replace("JavaScript", string.Empty), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheDocumentFormat_DoesNotDependOnClassNames()
    {
        var text = File.ReadAllText(Path.Combine(DesktopSources(), "Documents", "JsonSequenceDocumentSerializer.cs"));

        Assert.DoesNotContain("nameof(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType()", text.Replace("step.GetType().Name", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.", text, StringComparison.Ordinal); // no reflection-based serialization
    }

    [Fact]
    public void TheSequenceDocumentViewModel_WorksOnlyThroughTheAbstractions()
    {
        var text = File.ReadAllText(Path.Combine(DesktopSources(), "ViewModels", "SequenceDocumentViewModel.cs"));

        Assert.Contains("ISequenceDocumentStore", text, StringComparison.Ordinal);
        Assert.Contains("ISequenceFilePicker", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.File", text, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", text.Replace("SequenceDocumentFiles.", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", text, StringComparison.Ordinal);
    }
}
