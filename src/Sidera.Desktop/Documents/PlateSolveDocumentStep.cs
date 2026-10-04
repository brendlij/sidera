using System;
namespace Sidera.Desktop.Documents;
public sealed record PlateSolveDocumentStep(Guid Id, string? RigId, double ExposureSeconds) : DocumentLeafStep(Id);
