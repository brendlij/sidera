using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sidera.Desktop.Documents;

/// <summary>Where sequence documents live: reads and writes a document at a path. File I/O only; no encoding, no dialogs.</summary>
public interface ISequenceDocumentStore
{
    /// <exception cref="SequenceDocumentException">The file cannot be read, or is not a well-formed Sidera sequence document.</exception>
    Task<SequenceDocument> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Writes the document; an existing file at <paramref name="path"/> is only replaced by a complete new one.</summary>
    /// <exception cref="SequenceDocumentException">The document cannot be written.</exception>
    Task SaveAsync(string path, SequenceDocument document, CancellationToken cancellationToken = default);
}

/// <summary>
/// The name and extension of Sidera sequence files, as users see them. The extension <c>.astraseq</c> and the format identifier inside
/// (<c>astra-sequence</c>) are the names of the format from before the product was called Sidera: they stay, so that every existing
/// file keeps loading and the format version does not change because of a new product name.
/// </summary>
public static class SequenceDocumentFiles
{
    public const string Extension = ".astraseq";
    public const string Pattern = "*.astraseq";
    public const string FilterName = "Sidera Sequence (*.astraseq)";
    public const string DefaultFileName = "Sequence.astraseq";

    /// <summary>The path with the Sidera sequence extension: "M42" becomes "M42.astraseq", "M42.astraseq" stays as it is.</summary>
    public static string WithExtension(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path : path + Extension;
    }
}

/// <summary>
/// Reads and writes documents as files through an <see cref="ISequenceDocumentSerializer"/>. Saving is safe: the
/// document is encoded completely first, written to a temporary file next to the target, and only then moved over it,
/// so a failure at any point leaves an existing file as it was.
/// </summary>
public sealed class SequenceDocumentStore(ISequenceDocumentSerializer serializer) : ISequenceDocumentStore
{
    /// <summary>A store with the serializer of the current format version.</summary>
    public static SequenceDocumentStore CreateDefault() => new(new JsonSequenceDocumentSerializer());

    public async Task<SequenceDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await serializer.LoadAsync(stream, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, "Could not open sequence: the file was not found.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, "Could not open sequence: the file was not found.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, "Could not open sequence.", ex);
        }
    }

    public async Task SaveAsync(string path, SequenceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);

        // Encode everything before the destination is touched.
        using var content = new MemoryStream();
        await serializer.SaveAsync(content, document, cancellationToken);
        content.Position = 0;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            // Replaces the target in one step where the file system can; the target is never left half written.
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, "Could not save sequence.", ex);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the original error is what matters.
        }
    }
}
