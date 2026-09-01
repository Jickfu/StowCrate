using System.Globalization;
using Microsoft.Data.Sqlite;
using StowCrate.Application.LocalState;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed class ConfigDatabaseMaintenanceService : IConfigDatabaseMaintenanceService
{
    public async Task<ConfigDatabaseSnapshotResult> CreateSnapshotAsync(string databasePath, string snapshotPath,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(databasePath); var destination = Path.GetFullPath(snapshotPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Snapshot path must differ from config database path.", nameof(snapshotPath));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await BackupAsync(source, temporary, cancellationToken).ConfigureAwait(false);
            var diagnostic = await ValidateAsync(temporary, cancellationToken).ConfigureAwait(false);
            AtomicReplace(temporary, destination);
            return new(destination, diagnostic with { DatabasePath = destination });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<ConfigDatabaseIntegrityDiagnostic> ValidateAsync(string databasePath, CancellationToken cancellationToken)
        => ConfigDbOpenCoordinator.ValidateExistingAsync(databasePath, runIntegrityCheck: true, cancellationToken);

    public async Task<string> RestoreExplicitAsync(string corruptDatabasePath, string validatedSnapshotPath,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(corruptDatabasePath); var snapshot = Path.GetFullPath(validatedSnapshotPath);
        await ValidateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(target)) throw new LocalStateCorruptionException("Corrupt config database to preserve does not exist.");
        var suffix = ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var preserved = target + suffix;
        var temporary = target + ".restore-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (File.Exists(target)) File.Move(target, preserved);
        PreserveSidecar(target + "-wal", preserved + "-wal");
        PreserveSidecar(target + "-shm", preserved + "-shm");
        try
        {
            await BackupAsync(snapshot, temporary, cancellationToken).ConfigureAwait(false);
            await ValidateAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target);
            return preserved;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (!File.Exists(target) && File.Exists(preserved)) File.Move(preserved, target);
            throw;
        }
    }

    private static async Task BackupAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var destinationBuilder = new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        try
        {
            await using var source = new SqliteConnection(sourceBuilder.ConnectionString);
            await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
            await source.OpenAsync(cancellationToken).ConfigureAwait(false); await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            source.BackupDatabase(destination);
        }
        catch (SqliteException exception) { throw new LocalStateCorruptionException("SQLite Online Backup failed.", exception); }
    }

    private static void AtomicReplace(string temporary, string destination)
    {
        if (File.Exists(destination)) File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
        else File.Move(temporary, destination);
    }

    private static void PreserveSidecar(string source, string destination) { if (File.Exists(source)) File.Move(source, destination); }
}
