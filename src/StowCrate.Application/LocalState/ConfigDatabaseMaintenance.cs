namespace StowCrate.Application.LocalState;

public sealed record ConfigDatabaseIntegrityDiagnostic(string DatabasePath, ConfigDatabaseIdentity Identity, bool IntegrityOk, string Detail);
public sealed record ConfigDatabaseSnapshotResult(string SnapshotPath, ConfigDatabaseIntegrityDiagnostic Diagnostic);
public sealed record ConfigDatabaseRestoreResult(string PreservedCorruptDatabasePath, ConfigDatabaseSession Session);
public sealed record ConfigDatabaseRecoveryCandidate(string SnapshotPath, ConfigDatabaseIntegrityDiagnostic Diagnostic);
public enum ConfigDatabaseOpenRecoveryStatus { Opened, RecoveryCandidateAvailable, Fatal }
public sealed record ConfigDatabaseOpenRecoveryResult(ConfigDatabaseOpenRecoveryStatus Status, ConfigDatabaseSession? Session,
    ConfigDatabaseRecoveryCandidate? Candidate, LocalStateRepositoryException? Failure);

public interface IConfigDatabaseMaintenanceService
{
    Task<ConfigDatabaseSnapshotResult> CreateSnapshotAsync(string databasePath, string snapshotPath, CancellationToken cancellationToken);
    Task<ConfigDatabaseIntegrityDiagnostic> ValidateAsync(string databasePath, CancellationToken cancellationToken);
    Task<string> RestoreExplicitAsync(string corruptDatabasePath, string validatedSnapshotPath, CancellationToken cancellationToken);
}

public sealed class ConfigDatabaseRecoveryWorkflow(IConfigDatabaseMaintenanceService maintenance, IConfigDatabaseSessionOpener opener)
{
    public async Task<ConfigDatabaseOpenRecoveryResult> OpenOrReportRecoveryAsync(ConfigDatabaseOpenRequest request, string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await opener.OpenAsync(request, cancellationToken).ConfigureAwait(false);
            return new(ConfigDatabaseOpenRecoveryStatus.Opened, session, null, null);
        }
        catch (UnsupportedConfigDatabaseVersionException exception)
        {
            return new(ConfigDatabaseOpenRecoveryStatus.Fatal, null, null, exception);
        }
        catch (LocalStateCorruptionException exception)
        {
            var candidate = await DiscoverValidatedCandidateAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            return candidate is null
                ? new(ConfigDatabaseOpenRecoveryStatus.Fatal, null, null, exception)
                : new(ConfigDatabaseOpenRecoveryStatus.RecoveryCandidateAvailable, null, candidate, exception);
        }
    }

    public async Task<ConfigDatabaseRecoveryCandidate?> DiscoverValidatedCandidateAsync(string snapshotPath, CancellationToken cancellationToken)
    {
        try
        {
            var diagnostic = await maintenance.ValidateAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            return diagnostic.IntegrityOk ? new(snapshotPath, diagnostic) : null;
        }
        catch (LocalStateRepositoryException) { return null; }
    }

    public async Task<ConfigDatabaseRestoreResult> RestoreExplicitAsync(string corruptDatabasePath, string snapshotPath,
        CancellationToken cancellationToken)
    {
        var candidate = await DiscoverValidatedCandidateAsync(snapshotPath, cancellationToken).ConfigureAwait(false)
            ?? throw new LocalStateCorruptionException("No validated config database recovery candidate is available.");
        var preserved = await maintenance.RestoreExplicitAsync(corruptDatabasePath, candidate.SnapshotPath, cancellationToken).ConfigureAwait(false);
        var session = await opener.OpenAsync(new(corruptDatabasePath), cancellationToken).ConfigureAwait(false);
        return new(preserved, session);
    }
}

public sealed record ConfigDatabaseMaintenanceResult(ConfigDatabaseSnapshotResult Snapshot, int CompletedPublishIntentsRemoved);

public sealed class ConfigDatabaseMaintenanceWorkflow(IConfigDatabaseMaintenanceService snapshots, IArchiveUnitDurableStateStore archiveUnits)
{
    public async Task<ConfigDatabaseMaintenanceResult> RunDurabilityMaintenanceAsync(string databasePath, string snapshotPath,
        CancellationToken cancellationToken)
    {
        var snapshot = await snapshots.CreateSnapshotAsync(databasePath, snapshotPath, cancellationToken).ConfigureAwait(false);
        var cleaned = await archiveUnits.CleanupCompletedPublishIntentsAsync(cancellationToken).ConfigureAwait(false);
        return new(snapshot, cleaned);
    }
}
