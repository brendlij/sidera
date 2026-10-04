using Sidera.Desktop.Documents;

namespace Sidera.Desktop.Tests.Documents;

public sealed class SequenceDocumentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-store-tests-" + Guid.NewGuid().ToString("N"));

    public SequenceDocumentStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test; nothing depends on them.
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static SequenceDocument Document(double exposure = 300) => new("Session",
    [
        new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        new RepeatDocumentStep(Guid.NewGuid(), 3, [new ExposureDocumentStep(Guid.NewGuid(), "camera.main", exposure)]),
    ]);

    private static SequenceDocumentStore Store() => SequenceDocumentStore.CreateDefault();

    // A serializer that fails, or writes some content and then fails.
    private sealed class FailingSerializer(bool writeBeforeFailing) : ISequenceDocumentSerializer
    {
        public async Task SaveAsync(Stream stream, SequenceDocument document, CancellationToken cancellationToken)
        {
            if (writeBeforeFailing)
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            }

            throw new InvalidOperationException("encoding failed");
        }

        public Task<SequenceDocument> LoadAsync(Stream stream, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task ADocument_CanBeSavedToAFileAndOpenedAgain()
    {
        var path = PathOf("Session.astraseq");
        var document = Document();

        await Store().SaveAsync(path, document);
        var loaded = await Store().LoadAsync(path);

        Assert.True(File.Exists(path));
        Assert.Equal("Session", loaded.Name);
        Assert.Equal(document.Steps.Select(s => s.Id), loaded.Steps.Select(s => s.Id));
    }

    [Fact]
    public async Task SavingOverAnExistingFile_ReplacesItCompletely_AndLeavesNoTemporaryFile()
    {
        var path = PathOf("Session.astraseq");
        await Store().SaveAsync(path, Document(100));
        var second = Document(900);

        await Store().SaveAsync(path, second);

        var loaded = await Store().LoadAsync(path);
        var repeat = Assert.IsType<RepeatDocumentStep>(loaded.Steps[1]);
        Assert.Equal(900, Assert.IsType<ExposureDocumentStep>(Assert.Single(repeat.Children)).ExposureSeconds);
        Assert.Equal([path], Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailureWhileEncoding_LeavesAnExistingFileExactlyAsItWas(bool writeBeforeFailing)
    {
        var path = PathOf("Session.astraseq");
        await Store().SaveAsync(path, Document());
        var before = await File.ReadAllBytesAsync(path);

        var failing = new SequenceDocumentStore(new FailingSerializer(writeBeforeFailing));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SaveAsync(path, Document(1)));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(_directory));
        Assert.Equal(1, (await Store().LoadAsync(path)).Steps.Count(s => s is StartGuidingDocumentStep));
    }

    [Fact]
    public async Task AFailureWhileEncoding_CreatesNoFileAtAll_WhenThereWasNone()
    {
        var path = PathOf("New.astraseq");
        var failing = new SequenceDocumentStore(new FailingSerializer(writeBeforeFailing: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.SaveAsync(path, Document()));

        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task SavingIntoAFolderThatDoesNotExist_FailsWithAConciseError()
    {
        var path = PathOf(Path.Combine("missing", "Session.astraseq"));

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Store().SaveAsync(path, Document()));

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
        Assert.Equal("Could not save sequence.", ex.Message);
        Assert.NotNull(ex.InnerException); // the technical cause is kept
    }

    [Fact]
    public async Task SavingOverAFileThatIsLockedByAnotherProgram_FailsAndKeepsTheFile()
    {
        var path = PathOf("Session.astraseq");
        await Store().SaveAsync(path, Document());
        var before = await File.ReadAllBytesAsync(path);

        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (OperatingSystem.IsWindows())
            {
                var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Store().SaveAsync(path, Document(5)));
                Assert.Equal("Could not save sequence.", ex.Message);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(_directory));
        }
    }

    [Fact]
    public async Task OpeningAFileThatDoesNotExist_SaysSo()
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Store().LoadAsync(PathOf("nothing.astraseq")));

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
        Assert.Equal("Could not open sequence: the file was not found.", ex.Message);
    }

    [Fact]
    public async Task OpeningAFolderThatDoesNotExist_SaysSo()
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(
            () => Store().LoadAsync(PathOf(Path.Combine("missing", "x.astraseq"))));

        Assert.Equal("Could not open sequence: the file was not found.", ex.Message);
    }

    [Fact]
    public async Task OpeningAFileThatIsNoSequence_IsInvalid_WhateverItsExtension()
    {
        var path = PathOf("Notes.astraseq");
        await File.WriteAllTextAsync(path, "just some notes");

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Store().LoadAsync(path));

        Assert.Equal("Invalid Sidera sequence document.", ex.Message);
    }

    [Fact]
    public async Task OpeningAnEmptyFile_IsInvalid()
    {
        var path = PathOf("Empty.astraseq");
        await File.WriteAllBytesAsync(path, []);

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Store().LoadAsync(path));

        Assert.Equal("Invalid Sidera sequence document.", ex.Message);
    }

    [Fact]
    public async Task TheFileIsReadWhateverItsExtension_BecauseTheContentSaysWhatItIs()
    {
        var path = PathOf("Session.astraseq");
        await Store().SaveAsync(path, Document());
        var renamed = PathOf("Session.copy");
        File.Copy(path, renamed);

        var loaded = await Store().LoadAsync(renamed);

        Assert.Equal(2, loaded.Steps.Count);
    }

    [Theory]
    [InlineData("M42", "M42.astraseq")]
    [InlineData("M42.astraseq", "M42.astraseq")]
    [InlineData("M42.ASTRASEQ", "M42.ASTRASEQ")]
    [InlineData("M42.txt", "M42.txt.astraseq")]
    [InlineData("Session v1.2", "Session v1.2.astraseq")]
    [InlineData(@"C:\Sidera\M42", @"C:\Sidera\M42.astraseq")]
    public void TheSideraExtension_IsAddedWhereItIsMissing(string given, string expected)
    {
        Assert.Equal(expected, SequenceDocumentFiles.WithExtension(given));
    }

    [Fact]
    public void TheFileTypeIsNamedAndFilteredByTheSideraExtension_NotByTheEncoding()
    {
        Assert.Equal(".astraseq", SequenceDocumentFiles.Extension);
        Assert.Equal("*.astraseq", SequenceDocumentFiles.Pattern);
        Assert.Equal("Sidera Sequence (*.astraseq)", SequenceDocumentFiles.FilterName);
        Assert.DoesNotContain("json", SequenceDocumentFiles.FilterName + SequenceDocumentFiles.DefaultFileName, StringComparison.OrdinalIgnoreCase);
    }
}
