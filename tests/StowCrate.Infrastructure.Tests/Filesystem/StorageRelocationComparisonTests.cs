using StowCrate.Application.StorageMaintenance;
using StowCrate.Core.ChangeDetection;
using StowCrate.Infrastructure.Filesystem;

namespace StowCrate.Infrastructure.Tests.Filesystem;

public sealed partial class StorageRelocationPhysicalTests
{
    [Theory]
    [InlineData(0xef53L, 0U, true)]
    [InlineData(0xef53L, 0x80000U, true)]
    [InlineData(0xef53L, 0x40000000U, false)]
    [InlineData(0xef53L, 0x800U, false)]
    [InlineData(0x58465342L, 0U, false)]
    [InlineData(0x794c7630L, 0U, false)]
    public void OrdinalCapabilityRequiresKnownFilesystemAndDirectoryFlags(long type, uint flags, bool supported)
        => Assert.Equal(supported, LinuxOrdinalDirectoryProbe.Supports(type, flags));

    [Fact]
    public async Task ComparisonQueriesEveryExistingParentTwiceWithoutCreatingMissingDirectories()
    {
        using var fixture = new Fixture();
        var parent = Directory.CreateDirectory(Path.Combine(fixture.NewRoot, "资料")).FullName;
        var observation = ComparisonObservation(fixture, "资料/missing/deep/archive.7z");
        var calls = new List<string>();
        var probe = new StorageRelocationTargetComparisonProbe(path =>
        {
            calls.Add(path);
            return StorageRelocationPhysicalStore.InspectIdentity(path, true);
        });
        await probe.VerifyTargetsAsync(observation, fixture.Journal.Manifest.TransactionId, default);
        Assert.Equal(new[] { fixture.NewRoot, parent, fixture.NewRoot, parent }, calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Theory]
    [InlineData("unknown-child")]
    [InlineData("replace-root")]
    [InlineData("replace-parent")]
    [InlineData("create-parent")]
    [InlineData("rules-change")]
    [InlineData("wrong-identity")]
    [InlineData("cancel")]
    public async Task ComparisonRejectsCapabilityAndNamespaceDrift(string failure)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var parent = Path.Combine(fixture.NewRoot, "资料");
        if (failure != "create-parent") Directory.CreateDirectory(parent);
        var calls = 0;
        var probe = new StorageRelocationTargetComparisonProbe(path =>
        {
            calls++;
            var identity = StorageRelocationPhysicalStore.InspectIdentity(path, true);
            if (failure == "unknown-child" && path == parent) throw new NotSupportedException();
            if (failure == "rules-change" && calls == 3) throw new StorageRelocationComparisonUnavailableException();
            if (failure == "replace-root" && calls == 1)
            { Directory.Move(fixture.NewRoot, fixture.NewRoot + "-moved"); Directory.CreateDirectory(fixture.NewRoot); }
            if (failure == "replace-parent" && calls == 2)
            { Directory.Move(parent, parent + "-moved"); Directory.CreateDirectory(parent); }
            if (failure == "create-parent" && calls == 2) Directory.CreateDirectory(parent);
            if (failure == "wrong-identity") return new(identity.Provider, identity.EncodingVersion, "different");
            if (failure == "cancel") cancellation.Cancel();
            return identity;
        });
        var error = await Record.ExceptionAsync(() => probe.VerifyTargetsAsync(ComparisonObservation(fixture), fixture.Journal.Manifest.TransactionId, cancellation.Token));
        if (failure == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
        if (failure is "unknown-child" or "rules-change") Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
        Assert.False(File.Exists(fixture.Target));
        Assert.False(File.Exists(fixture.Temp));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task ComparisonChecksEmptyRootAndRejectsOverlongTransactionTemp()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var probe = new StorageRelocationTargetComparisonProbe(path =>
        { calls++; return StorageRelocationPhysicalStore.InspectIdentity(path, true); });
        var observation = ComparisonObservation(fixture);
        await probe.VerifyTargetsAsync(observation with { Inventory = observation.Inventory with { Entries = [] }, Entries = [] }, fixture.Journal.Manifest.TransactionId, default);
        Assert.Equal(2, calls);
        // final 在 255-byte 以内，但事务 temp 附加 UUID 后超长；必须在复制前拒绝。
        await Assert.ThrowsAsync<IOException>(() => probe.VerifyTargetsAsync(ComparisonObservation(fixture, new string('a', 200) + ".7z"), fixture.Journal.Manifest.TransactionId, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.NewRoot));
    }

    [Fact]
    public async Task NativeComparisonIsReadOnlyAndLinuxCiMustExerciseSupportedExtDirectory()
    {
        using var fixture = new Fixture();
        var probe = new StorageRelocationTargetComparisonProbe();
        var error = await Record.ExceptionAsync(() => probe.VerifyTargetsAsync(ComparisonObservation(fixture), fixture.Journal.Manifest.TransactionId, default));
        if (Environment.GetEnvironmentVariable("STOWCRATE_REQUIRE_EXT_COMPARISON") == "1") Assert.Null(error);
        else if (error is not null) Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
        if (!OperatingSystem.IsLinux()) Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.NewRoot));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.Source));
    }

    [Fact]
    public async Task SupportedNativeComparisonPreservesCaseAndUnicodeByteDistinctions()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var probe = new StorageRelocationTargetComparisonProbe();
        var error = await Record.ExceptionAsync(() => probe.VerifyTargetsAsync(ComparisonObservation(fixture), fixture.Journal.Manifest.TransactionId, default));
        if (error is not null)
        {
            Assert.IsType<StorageRelocationComparisonUnavailableException>(error);
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("STOWCRATE_REQUIRE_EXT_COMPARISON"));
            return;
        }
        // 这些文件仅由测试设置创建；生产 probe 不写探测文件，不采用未知对象。
        var upper = Path.Combine(fixture.NewRoot, "ARCHIVE.7z");
        var decomposed = Path.Combine(fixture.NewRoot, "e\u0301.7z");
        await File.WriteAllTextAsync(upper, "keep upper");
        await File.WriteAllTextAsync(decomposed, "keep decomposed");
        await probe.VerifyTargetsAsync(ComparisonObservation(fixture, "archive.7z"), fixture.Journal.Manifest.TransactionId, default);
        await probe.VerifyTargetsAsync(ComparisonObservation(fixture, "é.7z"), fixture.Journal.Manifest.TransactionId, default);
        await Assert.ThrowsAsync<IOException>(() => probe.VerifyTargetsAsync(ComparisonObservation(fixture, "ARCHIVE.7z"), fixture.Journal.Manifest.TransactionId, default));
        Assert.Equal("keep upper", await File.ReadAllTextAsync(upper));
        Assert.Equal("keep decomposed", await File.ReadAllTextAsync(decomposed));
        Assert.Equal(2, Directory.EnumerateFiles(fixture.NewRoot).Count());
    }

    private static StorageRelocationPhysicalInventory ComparisonObservation(Fixture fixture, string? relative = null)
    {
        var inventory = Inventory(fixture);
        if (relative is not null) inventory = inventory with { Entries = [inventory.Entries[0] with { RelativePath = new RelativeStoragePath(relative) }] };
        return new(inventory, [.. fixture.Journal.Manifest.Roots],
            [.. inventory.Entries.Select(x => new StorageRelocationPlacementObservation(x, fixture.Journal.Manifest.Entries[0].OldIdentity))], []);
    }
}
