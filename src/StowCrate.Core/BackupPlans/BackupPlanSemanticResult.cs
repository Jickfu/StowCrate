using System.Collections.Immutable;

namespace StowCrate.Core.BackupPlans;

public enum BackupPlanSemanticErrorCode
{
    UnsupportedDocumentSemantics,
    InvalidValue,
    DuplicateSourceId,
    DuplicateArchiveUnitId,
    DuplicateExternalSourceId,
    DuplicateSecretSlotId,
    UnknownSourceReference,
    UnknownArchiveUnitReference,
    UnknownSecretSlotReference,
    DuplicateArchiveUnitDeclaration,
    InvalidRulePattern,
    DuplicateScheduleTrigger,
    ExternalOwnershipCollision,
    ExternalCrossesDeclaredChildBoundary
}

public sealed record BackupPlanSemanticError(
    BackupPlanSemanticErrorCode Code,
    string Message,
    string? Location = null);

public sealed class BackupPlanSemanticResult
{
    public BackupPlanSemanticResult(PortableBackupPlan? plan, IEnumerable<BackupPlanSemanticError> errors)
    {
        Plan = plan;
        Errors = [.. errors];
    }

    public PortableBackupPlan? Plan { get; }
    public ImmutableArray<BackupPlanSemanticError> Errors { get; }
    public bool IsSuccess => Plan is not null && Errors.IsEmpty;
}

public static class PortableSemanticsSupport
{
    public const int Rules = 1;
    public const int Archive = 1;
    public const int OutputPathEncoding = 1;

    public static ImmutableArray<BackupPlanSemanticError> Validate(PortableSemanticsPins pins)
    {
        var errors = ImmutableArray.CreateBuilder<BackupPlanSemanticError>();
        AddIfUnsupported(errors, pins.Rules, Rules, "rules");
        AddIfUnsupported(errors, pins.Archive, Archive, "archive");
        AddIfUnsupported(errors, pins.OutputPathEncoding, OutputPathEncoding, "outputPathEncoding");
        return errors.ToImmutable();
    }

    private static void AddIfUnsupported(
        ImmutableArray<BackupPlanSemanticError>.Builder errors,
        int actual,
        int supported,
        string property)
    {
        if (actual != supported)
        {
            errors.Add(new BackupPlanSemanticError(
                BackupPlanSemanticErrorCode.UnsupportedDocumentSemantics,
                $"Document semantics '{property}' version {actual} is unsupported; this reader supports {supported}.",
                $"/semantics/{property}"));
        }
    }
}
