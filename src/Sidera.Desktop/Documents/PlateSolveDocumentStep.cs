using System;
namespace Sidera.Desktop.Documents;
public sealed record PlateSolveDocumentStep(Guid Id, string? RigId, double ExposureSeconds) : DocumentLeafStep(Id);
public sealed record SlewAndCenterDocumentStep(
    Guid Id, string? MountId, string? RigId, double RaHours, double DecDegrees, double ToleranceArcseconds, int MaxAttempts, double ExposureSeconds) : DocumentLeafStep(Id);
public sealed record SyncMountDocumentStep(Guid Id, string? MountId) : DocumentLeafStep(Id);
