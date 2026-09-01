using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Application.BackupPlans.Documents;

public sealed record AuthoritativePlanSnapshot(PortableBackupPlan Plan, PlanAuthority Authority, long? ManagedRevision, string? FileDocumentPath, bool IsActive);
public enum AuthoritativePlanConflictCode { AlreadyExists, NotFound, IdentityConflict, AuthorityConflict, RevisionConflict }
public sealed class AuthoritativePlanConflictException(AuthoritativePlanConflictCode code, string message) : Exception(message) { public AuthoritativePlanConflictCode Code { get; } = code; }

public sealed class AuthoritativePlanWorkflow(IPlanRegistrationStore store, IBackupPlanDocumentSource documents)
{
    public async Task<AuthoritativePlanSnapshot> LoadAsync(PlanId planId, CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw Conflict(AuthoritativePlanConflictCode.NotFound, "Plan is not registered.");
        ValidatedBackupPlanDocument document = state.Registration.Authority switch
        {
            PlanAuthority.Managed when state.ManagedDocument is not null => documents.ReadCanonicalPayload(state.ManagedDocument.CanonicalUtf8Payload),
            PlanAuthority.FileBacked when state.Registration.FileDocumentPath is not null => await documents.ReadFileAsync(state.Registration.FileDocumentPath, cancellationToken).ConfigureAwait(false),
            _ => throw new LocalStateCorruptionException("Plan registration authority payload is inconsistent.")
        };
        if (document.Plan.Id != planId) throw new BackupPlanDocumentSourceException("Authoritative document PlanId differs from registration.");
        return new(document.Plan, state.Registration.Authority, state.ManagedDocument?.Revision, state.Registration.FileDocumentPath, state.Registration.IsActive);
    }

    public async Task<AuthoritativePlanSnapshot> CreateManagedAsync(PortableBackupPlan plan, CancellationToken cancellationToken)
    {
        if (await store.LoadAsync(plan.Id, cancellationToken).ConfigureAwait(false) is not null) throw Conflict(AuthoritativePlanConflictCode.AlreadyExists, "PlanId is already registered.");
        var canonical = Canonical(plan);
        var saved = await store.SaveManagedAsync(new(plan.Id, PlanAuthority.Managed, null, true), canonical.CanonicalUtf8Payload, null, cancellationToken).ConfigureAwait(false);
        return new(canonical.Plan, PlanAuthority.Managed, saved.Revision, null, true);
    }

    public async Task<AuthoritativePlanSnapshot> UpdateManagedAsync(PortableBackupPlan plan, long expectedRevision, CancellationToken cancellationToken)
    {
        var existing = await store.LoadAsync(plan.Id, cancellationToken).ConfigureAwait(false) ?? throw Conflict(AuthoritativePlanConflictCode.NotFound, "Plan is not registered.");
        if (existing.Registration.Authority is not PlanAuthority.Managed) throw Conflict(AuthoritativePlanConflictCode.AuthorityConflict, "Ordinary Managed update cannot change Plan authority.");
        try
        {
            var canonical = Canonical(plan); var saved = await store.SaveManagedAsync(existing.Registration, canonical.CanonicalUtf8Payload, expectedRevision, cancellationToken).ConfigureAwait(false);
            return new(canonical.Plan, PlanAuthority.Managed, saved.Revision, null, existing.Registration.IsActive);
        }
        catch (LocalStateConcurrencyException exception) { throw new AuthoritativePlanConflictException(AuthoritativePlanConflictCode.RevisionConflict, exception.Message); }
    }

    public async Task<AuthoritativePlanSnapshot> RegisterFileBackedAsync(string path, CancellationToken cancellationToken)
    {
        var incoming = await documents.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        var existing = await store.LoadAsync(incoming.Plan.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            var loaded = await LoadAsync(incoming.Plan.Id, cancellationToken).ConfigureAwait(false);
            if (CandidateFingerprintCalculator.ComputePlanSemantic(loaded.Plan) != CandidateFingerprintCalculator.ComputePlanSemantic(incoming.Plan))
                throw Conflict(AuthoritativePlanConflictCode.IdentityConflict, "The same PlanId has different semantic configuration.");
            if (loaded.Authority is not PlanAuthority.FileBacked || !string.Equals(loaded.FileDocumentPath, incoming.CanonicalSourcePath, StringComparison.Ordinal))
                throw Conflict(AuthoritativePlanConflictCode.AuthorityConflict, "Register cannot silently change authority or registration path.");
            return loaded;
        }
        await store.SaveFileBackedAsync(new(incoming.Plan.Id, PlanAuthority.FileBacked, incoming.CanonicalSourcePath!, true), cancellationToken).ConfigureAwait(false);
        return new(incoming.Plan, PlanAuthority.FileBacked, null, incoming.CanonicalSourcePath, true);
    }

    public async Task<AuthoritativePlanSnapshot> ConvertToManagedAsync(PlanId planId, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (existing.Authority is not PlanAuthority.FileBacked) throw Conflict(AuthoritativePlanConflictCode.AuthorityConflict, "Plan is not File-backed.");
        var canonical = Canonical(existing.Plan);
        var saved = await store.SaveManagedAsync(new(planId, PlanAuthority.Managed, null, existing.IsActive), canonical.CanonicalUtf8Payload, null, cancellationToken).ConfigureAwait(false);
        return new(canonical.Plan, PlanAuthority.Managed, saved.Revision, null, existing.IsActive);
    }

    public async Task<AuthoritativePlanSnapshot> ConvertToFileBackedAsync(PlanId planId, string path, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(planId, cancellationToken).ConfigureAwait(false);
        if (existing.Authority is not PlanAuthority.Managed) throw Conflict(AuthoritativePlanConflictCode.AuthorityConflict, "Plan is not Managed.");
        var incoming = await documents.ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (incoming.Plan.Id != planId) throw Conflict(AuthoritativePlanConflictCode.IdentityConflict, "Conversion target has a different PlanId.");
        await store.SaveFileBackedAsync(new(planId, PlanAuthority.FileBacked, incoming.CanonicalSourcePath!, existing.IsActive), cancellationToken).ConfigureAwait(false);
        return new(incoming.Plan, PlanAuthority.FileBacked, null, incoming.CanonicalSourcePath, existing.IsActive);
    }

    public Task SetActiveAsync(PlanId planId, bool isActive, CancellationToken cancellationToken) => store.SetActiveAsync(planId, isActive, cancellationToken);

    private ValidatedBackupPlanDocument Canonical(PortableBackupPlan plan)
    {
        // 确定性序列化由 document source 负责；经内存端口投影可避免 Application 感知文档 DTO。
        return documents.Write(plan);
    }
    private static AuthoritativePlanConflictException Conflict(AuthoritativePlanConflictCode code, string message) => new(code, message);
}
