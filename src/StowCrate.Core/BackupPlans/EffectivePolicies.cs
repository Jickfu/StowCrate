namespace StowCrate.Core.BackupPlans;

public sealed record EffectiveArchiveSpec(
    PortableArchiveFormat Format,
    PortableCompressionPreset CompressionPreset,
    AuthoredProtection Protection);

public static class ArchiveSpecPolicy
{
    public static EffectiveArchiveSpec Resolve(
        AuthoredArchiveSpec defaultSpec,
        AuthoredArchiveSpecOverride? authoredOverride)
    {
        ArgumentNullException.ThrowIfNull(defaultSpec);
        return new EffectiveArchiveSpec(
            authoredOverride?.Format ?? defaultSpec.Format,
            authoredOverride?.CompressionPreset ?? defaultSpec.CompressionPreset,
            authoredOverride?.Protection ?? defaultSpec.Protection);
    }
}

public abstract record EffectiveHistoryPolicy;
public sealed record EffectiveHistoryDisabled : EffectiveHistoryPolicy;
public sealed record EffectiveHistoryEnabled(AuthoredRetentionPolicy Retention) : EffectiveHistoryPolicy;

public static class HistoryPolicy
{
    public static EffectiveHistoryPolicy Resolve(
        AuthoredHistoryPolicy defaultPolicy,
        AuthoredHistoryOverride? authoredOverride)
    {
        ArgumentNullException.ThrowIfNull(defaultPolicy);
        return authoredOverride switch
        {
            null or HistoryInherit => ResolveDefault(defaultPolicy),
            HistoryOverrideDisabled => new EffectiveHistoryDisabled(),
            HistoryOverrideEnabled enabled => new EffectiveHistoryEnabled(enabled.Retention),
            _ => throw new InvalidOperationException($"Unknown history override {authoredOverride.GetType().Name}.")
        };
    }

    private static EffectiveHistoryPolicy ResolveDefault(AuthoredHistoryPolicy defaultPolicy) => defaultPolicy switch
    {
        HistoryDisabled => new EffectiveHistoryDisabled(),
        HistoryEnabled enabled => new EffectiveHistoryEnabled(enabled.Retention),
        _ => throw new InvalidOperationException($"Unknown history policy {defaultPolicy.GetType().Name}.")
    };
}
