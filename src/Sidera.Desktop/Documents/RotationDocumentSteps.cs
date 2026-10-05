using System;

namespace Sidera.Desktop.Documents;

public sealed record RotateToAngleDocumentStep(Guid Id, string? RigId, double SkyRotationDegrees) : DocumentLeafStep(Id);

public sealed record RotateAndVerifyDocumentStep(
    Guid Id, string? RigId, double SkyRotationDegrees, double ToleranceDegrees, int MaxAttempts, double ExposureSeconds) : DocumentLeafStep(Id);

public sealed record CenterAndRotateDocumentStep(
    Guid Id, string? MountId, string? RigId, double RaHours, double DecDegrees, double ToleranceArcseconds, int MaxCenteringAttempts,
    double SkyRotationDegrees, double RotationToleranceDegrees, int MaxRotationAttempts, int MaxRounds, double ExposureSeconds, string? TargetName = null) : DocumentLeafStep(Id);
