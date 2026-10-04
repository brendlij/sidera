using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sidera.Desktop.Documents;

/// <summary>
/// Turns a <see cref="SequenceDocument"/> into the content of a <c>.astraseq</c> file and back. What that content looks
/// like is the implementation's business and may change between versions of the format; callers must not assume it is
/// text, or any particular encoding. A serializer reads every version it knows and writes the current one.
/// </summary>
public interface ISequenceDocumentSerializer
{
    /// <summary>Writes the whole document to <paramref name="stream"/>.</summary>
    /// <exception cref="SequenceDocumentException">The document cannot be written in this format.</exception>
    Task SaveAsync(Stream stream, SequenceDocument document, CancellationToken cancellationToken);

    /// <summary>Reads a whole document from <paramref name="stream"/>.</summary>
    /// <exception cref="SequenceDocumentException">The content is not a readable, well-formed Sidera sequence document.</exception>
    Task<SequenceDocument> LoadAsync(Stream stream, CancellationToken cancellationToken);
}
