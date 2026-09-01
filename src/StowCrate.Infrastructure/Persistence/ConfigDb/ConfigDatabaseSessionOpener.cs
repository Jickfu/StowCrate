using StowCrate.Application.LocalState;

namespace StowCrate.Infrastructure.Persistence.ConfigDb;

public sealed class ConfigDatabaseSessionOpener : IConfigDatabaseSessionOpener
{
    public async Task<ConfigDatabaseSession> OpenAsync(ConfigDatabaseOpenRequest request, CancellationToken cancellationToken)
    {
        var repository = await ConfigDbOpenCoordinator.OpenAsync(
            request.DatabasePath, request.NewDatabaseId, request.NewDeviceId, cancellationToken).ConfigureAwait(false);
        var identity = await repository.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new LocalStateCorruptionException("Opened config database has no database/device identity.");
        return new(identity, repository, repository, repository);
    }
}
