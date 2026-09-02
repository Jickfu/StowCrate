using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StowCrate.Application.BackupPlans.Resolution;
using StowCrate.Application.LocalState;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed class ConfigDbOpenCoordinator
{
    public const int SupportedSchemaVersion = 3;

    public static async Task<ConfigDbRepository> OpenAsync(string databasePath, Guid? newDatabaseId = null, DeviceId? newDeviceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var exists = File.Exists(fullPath);
        if (exists) await ValidateExistingAsync(fullPath, runIntegrityCheck: false, cancellationToken).ConfigureAwait(false);
        else if (newDatabaseId is null || newDeviceId is null) throw new ArgumentException("New config database requires database and device identities.");

        var factory = new ConfigDbContextFactory(fullPath);
        await using var context = factory.Create();
        try
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                context.DatabaseMetadata.Add(new DatabaseMetadataEntity
                {
                    SingletonKey = 1, SchemaVersion = SupportedSchemaVersion,
                    DatabaseId = DurableCodecs.Uuid(newDatabaseId!.Value), DeviceId = DurableCodecs.Uuid(newDeviceId!.Value.Value),
                    CreatedAtUtcMs = DurableCodecs.Utc(DateTimeOffset.UtcNow)
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException)
        {
            throw new LocalStateCorruptionException("Config database could not be opened or migrated.", exception);
        }

        return new ConfigDbRepository(factory);
    }

    public static async Task<ConfigDatabaseIntegrityDiagnostic> ValidateExistingAsync(string path, bool runIntegrityCheck, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        try
        {
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // 必须先独立判定 schema version，future database不得被旧应用继续猜测列布局。
            command.CommandText = "SELECT SchemaVersion FROM DatabaseMetadata WHERE SingletonKey=1";
            var versionValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (versionValue is not long version || version < 1) throw new LocalStateCorruptionException("DatabaseMetadata.SchemaVersion is missing or invalid.");
            if (version > SupportedSchemaVersion) throw new UnsupportedConfigDatabaseVersionException(checked((int)version));
            if (version < 1 || version > SupportedSchemaVersion) throw new LocalStateCorruptionException($"Config database schema version {version} requires an explicit supported migration path.");

            command.CommandText = "SELECT DatabaseId,DeviceId,CreatedAtUtcMs FROM DatabaseMetadata WHERE SingletonKey=1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new LocalStateCorruptionException("DatabaseMetadata is missing.");
            var identity = new ConfigDatabaseIdentity(DurableCodecs.Uuid((byte[])reader.GetValue(0)), new(DurableCodecs.Uuid((byte[])reader.GetValue(1))),
                checked((int)version), DurableCodecs.Utc(reader.GetInt64(2)));
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!runIntegrityCheck) return new(path, identity, true, "Metadata probe passed.");
            command.CommandText = "PRAGMA integrity_check";
            var integrity = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? "missing result";
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) throw new LocalStateCorruptionException("SQLite integrity check failed.");
            return new(path, identity, true, "SQLite integrity_check passed.");
        }
        catch (LocalStateRepositoryException) { throw; }
        catch (SqliteException exception) { throw new LocalStateCorruptionException("Config database metadata probe failed.", exception); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException or InvalidCastException)
        { throw new LocalStateCorruptionException("Config database metadata is corrupt.", exception); }
    }
}

internal sealed class ConfigDbContextFactory(string databasePath)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ConnectionString;
    public ConfigDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connectionString).AddInterceptors(ConfigDbConnectionInterceptor.Instance).Options;
        return new ConfigDbContext(options);
    }
}

internal sealed class ConfigDbConnectionInterceptor : DbConnectionInterceptor
{
    public static ConfigDbConnectionInterceptor Instance { get; } = new();
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Configure(connection);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) => ConfigureAsync(connection, cancellationToken);
    private static void Configure(DbConnection connection) { using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;"; command.ExecuteNonQuery(); }
    private static async Task ConfigureAsync(DbConnection connection, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;"; await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
}
