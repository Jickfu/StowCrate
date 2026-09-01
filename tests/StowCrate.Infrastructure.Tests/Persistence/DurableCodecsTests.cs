using System.Text;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Persistence.ConfigDb;

namespace StowCrate.Infrastructure.Tests.Persistence;

public sealed class DurableCodecsTests
{
    [Fact]
    public void UuidUsesRfcNetworkByteOrderKnownVector()
    {
        var value = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var expected = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        Assert.Equal(expected, DurableCodecs.Uuid(value));
        Assert.Equal(value, DurableCodecs.Uuid(expected));
        Assert.NotEqual(value.ToByteArray(), expected);
    }

    [Fact]
    public void FrozenScalarEncodingsRoundTripAndRejectInvalidValues()
    {
        var digest = Sha256Digest.Hash("payload"u8);
        Assert.Equal(digest, DurableCodecs.Digest(DurableCodecs.Digest(digest)));
        Assert.Throws<LocalStateCorruptionException>(() => DurableCodecs.Digest(new byte[31]));
        Assert.True(DurableCodecs.Boolean(1));
        Assert.False(DurableCodecs.Boolean(0));
        Assert.Throws<LocalStateCorruptionException>(() => DurableCodecs.Boolean(2));
        Assert.Equal("TAR_ZSTD", DurableCodecs.Token(PortableArchiveFormat.TarZstd));
        Assert.Throws<LocalStateCorruptionException>(() => DurableCodecs.ArchiveFormat("tar_zstd"));
    }

    [Fact]
    public void EveryV1StableEnumTokenIsExplicitAndRoundTrips()
    {
        Assert.Equal(PlanAuthority.Managed, DurableCodecs.Authority(DurableCodecs.Token(PlanAuthority.Managed)));
        Assert.Equal(PlanAuthority.FileBacked, DurableCodecs.Authority(DurableCodecs.Token(PlanAuthority.FileBacked)));
        foreach (var value in Enum.GetValues<PortableArchiveFormat>()) Assert.Equal(value, DurableCodecs.ArchiveFormat(DurableCodecs.Token(value)));
        foreach (var value in Enum.GetValues<ArchiveVersionLifecycle>()) Assert.Equal(value, DurableCodecs.Lifecycle(DurableCodecs.Token(value)));
        foreach (var value in Enum.GetValues<PublishIntentStage>()) Assert.Equal(value, DurableCodecs.PublishStage(DurableCodecs.Token(value)));
        foreach (var value in Enum.GetValues<ScheduleInstallationStatus>()) Assert.Equal(value, DurableCodecs.ScheduleStatus(DurableCodecs.Token(value)));
        foreach (var value in Enum.GetValues<MaintenanceStatus>()) Assert.Equal(value, DurableCodecs.MaintenanceStatus(DurableCodecs.Token(value)));
        foreach (var value in Enum.GetValues<MaintenanceKind>()) Assert.Equal(value, DurableCodecs.MaintenanceKind(DurableCodecs.Token(value)));
    }

    [Fact]
    public void TimePathAndUtf8AreCanonical()
    {
        var instant = DateTimeOffset.Parse("2026-08-31T12:34:56.7891234+08:00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("2026-08-31T04:34:56.7890000+00:00", DurableCodecs.Utc(DurableCodecs.Utc(instant)).ToString("O"));
        Assert.Equal("é/file", DurableCodecs.LogicalPath("e\u0301/file"));
        Assert.Equal("payload", Encoding.UTF8.GetString(DurableCodecs.Utf8("payload"u8.ToArray())));
        Assert.Throws<LocalStateCorruptionException>(() => DurableCodecs.Utf8(new byte[] { 0xff }));
    }
}
