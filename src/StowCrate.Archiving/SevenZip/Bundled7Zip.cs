using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Core.BackupPlans;

namespace StowCrate.Archiving.SevenZip;

public sealed record Bundled7ZipAsset(string RuntimeIdentifier, string PackageName, string PackageSha256,
    string ExecutableRelativePath, string ExecutableSha256);

public static class Bundled7ZipDescriptor
{
    public const string Version = "26.02";
    public const string ReleaseDate = "2026-06-25";
    public const string ReleaseBaseUrl = "https://github.com/ip7z/7zip/releases/download/26.02/";
    public const string SecretTransportSemantics = "stdin-prompt-spike-failed-v1";
    public static IReadOnlyDictionary<string, Bundled7ZipAsset> Assets { get; } = new Dictionary<string, Bundled7ZipAsset>(StringComparer.Ordinal)
    {
        ["win-x64"] = new("win-x64", "7z2602-extra.7z", "081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a", "7za.exe", "35d4d69d7cd6cb44558f208c3b1334268013f9daf82d2dda848893a1c30c59c2"),
        ["win-arm64"] = new("win-arm64", "7z2602-extra.7z", "081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a", "7za.exe", "cadbd34657713935222eb14fddbcdd51953501b44c749d9a029fab8f1c46be7e"),
        ["linux-x64"] = new("linux-x64", "7z2602-linux-x64.tar.xz", "41aaba7b1235304ab5aa0624530c67ae829496cd29e875925271efdccc28c03e", "7zz", "1676a968815b92e865bc0ffeecee3fa284ba4402bf23dc2bec2412c4b502e922"),
        ["linux-arm64"] = new("linux-arm64", "7z2602-linux-arm64.tar.xz", "70ea6cc737ae1495ea2d7eb20ef3120fe579bd3f1a83a9d2362b62ec5bde2bba", "7zz", "41ca798f0c0652c435cbdd9c3ba49d703c9410c597f40a5cd336304b3964c674"),
        ["osx-x64"] = new("osx-x64", "7z2602-mac.tar.xz", "1cf6760579502f87e591ff5c73a005ec50b3e4d6f507e8b038382d563c3175b9", "7zz", "9c56cf3379a0d8544e9244958b96fdc7c17f9ce70f5a160eb2b41f5f3df96d8c"),
        ["osx-arm64"] = new("osx-arm64", "7z2602-mac.tar.xz", "1cf6760579502f87e591ff5c73a005ec50b3e4d6f507e8b038382d563c3175b9", "7zz", "9c56cf3379a0d8544e9244958b96fdc7c17f9ce70f5a160eb2b41f5f3df96d8c")
    };
}

public sealed class Bundled7ZipLocator
{
    private readonly string bundleRoot;
    public Bundled7ZipLocator(string bundleRoot)
    {
        this.bundleRoot = bundleRoot;
        Asset = Bundled7ZipDescriptor.Assets.TryGetValue(CurrentRid(), out var asset)
            ? asset : throw new PlatformNotSupportedException("No pinned 7-Zip asset exists for the current RID.");
    }
    public Bundled7ZipAsset Asset { get; }
    public string ExecutablePath => Path.GetFullPath(Path.Combine(bundleRoot, Asset.RuntimeIdentifier, Asset.ExecutableRelativePath));
    private static string CurrentRid() => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) switch
    {
        (true, _, _, System.Runtime.InteropServices.Architecture.X64) => "win-x64",
        (true, _, _, System.Runtime.InteropServices.Architecture.Arm64) => "win-arm64",
        (_, true, _, System.Runtime.InteropServices.Architecture.X64) => "linux-x64",
        (_, true, _, System.Runtime.InteropServices.Architecture.Arm64) => "linux-arm64",
        (_, _, true, System.Runtime.InteropServices.Architecture.X64) => "osx-x64",
        (_, _, true, System.Runtime.InteropServices.Architecture.Arm64) => "osx-arm64",
        _ => throw new PlatformNotSupportedException("Unsupported runtime architecture for bundled 7-Zip.")
    };
}

public sealed record Bundled7ZipProbeResult(bool IsAvailable, string? ExecutablePath, string? Failure, string? Version);

public sealed class Bundled7ZipCapabilityProbe(Bundled7ZipLocator locator, SevenZipProcessRunner runner)
{
    public async Task<Bundled7ZipProbeResult> ProbeAsync(CancellationToken cancellationToken)
        => await ProbeAsync(locator.ExecutablePath, locator.Asset.ExecutableSha256, cancellationToken).ConfigureAwait(false);

    public async Task<Bundled7ZipProbeResult> ProbeAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(false, null, "Bundled 7-Zip executable is missing.", null);
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!StringComparer.Ordinal.Equals(hash, expectedSha256)) return new(false, null, "Bundled 7-Zip executable integrity mismatch.", null);
        var result = await runner.RunAsync(new(path, null, ["i"], null), cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(result.StandardOutput, @"7-Zip.*?([0-9]{2}\.[0-9]{2})", RegexOptions.CultureInvariant);
        if (result.ExitCode != 0 || !match.Success || match.Groups[1].Value != Bundled7ZipDescriptor.Version)
            return new(false, null, "Bundled 7-Zip version/capability probe failed.", match.Success ? match.Groups[1].Value : null);
        if (!result.StandardOutput.Contains(" 7z ", StringComparison.Ordinal) || !result.StandardOutput.Contains(" zip ", StringComparison.OrdinalIgnoreCase))
            return new(false, null, "Bundled 7-Zip lacks required 7z/ZIP formats.", match.Groups[1].Value);
        return new(true, path, null, match.Groups[1].Value);
    }
}

public sealed class Bundled7ZipCapabilityResolver(Bundled7ZipProbeResult probe) : IArchiveCapabilityResolver
{
    public ArchiveCapabilityResolution Resolve(ArchiveCapabilityRequirements requirements, int archiveSemanticsVersion)
    {
        var spec = requirements.ArchiveSpec;
        if (!probe.IsAvailable) return new(null, probe.Failure ?? "Bundled 7-Zip is unavailable.");
        if (spec.Format is not (PortableArchiveFormat.SevenZip or PortableArchiveFormat.Zip)) return new(null, "Bundled 7-Zip M4.2 supports only SevenZip and ZIP.");
        if (spec.Protection is PrivacyProtection) return new(null, "Privacy recovery envelope is not frozen.");
        if (spec.Protection is SecureProtection) return new(null, "Secure is unsupported: redirected-stdin password transport was not reliable in the 7-Zip 26.02 spike.");
        if (requirements.RequiresSymbolicLinks) return new(null, "Symbolic-link fidelity has not passed the cross-platform backend matrix.");
        var metadata = new ArchiveMetadataFeatures(true, StowCrate.Core.Filesystem.SourceMetadata.None);
        if (!metadata.Satisfies(requirements.RequiredMetadataFeatures)) return new(null, "Required metadata features have not passed the cross-platform backend matrix.");
        var semantics = $"7zip/{Bundled7ZipDescriptor.Version};archive={archiveSemanticsVersion};format={spec.Format};preset={SevenZipArgumentMapping.Level(spec.CompressionPreset)};protection=None;links=none;mtime=true;metadataFlags=none;volume=single;secret={Bundled7ZipDescriptor.SecretTransportSemantics}";
        return new(new(spec.Format, spec.CompressionPreset, spec.Protection, ArchiveLinkSemantics.NoLinks,
            metadata, true, semantics), null);
    }
}
