using StowCrate.Core.BackupPlans;

namespace StowCrate.Core.Tests.BackupPlans;

public sealed class EffectivePolicyTests
{
    [Fact]
    public void ArchiveOverrideResolvesEachComponentAgainstAuthoredDefault()
    {
        var defaultSpec = new AuthoredArchiveSpec(
            PortableArchiveFormat.SevenZip,
            PortableCompressionPreset.Standard,
            new SecureProtection(new SecretSlotId(Guid.Parse("11111111-1111-4111-8111-111111111111"))));
        var authoredOverride = new AuthoredArchiveSpecOverride(
            PortableArchiveFormat.Zip,
            null,
            new PrivacyProtection());

        var effective = ArchiveSpecPolicy.Resolve(defaultSpec, authoredOverride);

        Assert.Equal(PortableArchiveFormat.Zip, effective.Format);
        Assert.Equal(PortableCompressionPreset.Standard, effective.CompressionPreset);
        Assert.IsType<PrivacyProtection>(effective.Protection);
    }

    [Fact]
    public void HistoryResolutionDistinguishesInheritDisabledAndEnabled()
    {
        var defaultPolicy = new HistoryEnabled(new KeepAllRetention());

        var omitted = HistoryPolicy.Resolve(defaultPolicy, null);
        var inherited = HistoryPolicy.Resolve(defaultPolicy, new HistoryInherit());
        var disabled = HistoryPolicy.Resolve(defaultPolicy, new HistoryOverrideDisabled());
        var enabled = HistoryPolicy.Resolve(
            new HistoryDisabled(),
            new HistoryOverrideEnabled(new KeepLastVersionsRetention(3)));

        Assert.IsType<EffectiveHistoryEnabled>(omitted);
        Assert.IsType<EffectiveHistoryEnabled>(inherited);
        Assert.IsType<EffectiveHistoryDisabled>(disabled);
        Assert.Equal(3, Assert.IsType<KeepLastVersionsRetention>(Assert.IsType<EffectiveHistoryEnabled>(enabled).Retention).Count);
    }
}
