using StowCrate.Application.BackupPlans.Candidates;
using StowCrate.Application.LocalState;
using StowCrate.Core.BackupPlans;
using StowCrate.Core.ChangeDetection;
using StowCrate.Core.Filesystem;
using StowCrate.Core.Paths;

namespace StowCrate.Application.Archiving;

public sealed class ArchiveBuildWorkflow(
    IArchiveInputMaterializer materializer,
    IArchiveFormatWriter writer,
    IArchiveArtifactVerifier verifier,
    IArchiveManifestCodec manifestCodec,
    IArchiveSecretLeaseProvider? secrets = null)
{
    public async Task<ArchiveBuildResult> BuildAsync(ArchiveBuildRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<ArchiveBuildDiagnostic>();
        MaterializedArchiveInput? input = null;
        SecretMaterialLease? secret = null;
        var preservePartial = false;
        try
        {
            if (!request.Archive.Capability.ExactlyMatches(request.Archive.Candidate.Unit.ArchiveSpec))
                return await CompleteAsync(Failure(ArchiveBuildFailureCode.UnsupportedArchiveCapability, "Resolved capability does not exactly match the effective ArchiveSpec."), false).ConfigureAwait(false);

            var manifestBytes = manifestCodec.Write(request);
            input = await materializer.MaterializeAsync(request, manifestBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Archive.SecureRequirement is { } requirement)
            {
                secret = secrets is null ? null : await secrets.OpenAsync(request.PlanId, requirement, cancellationToken).ConfigureAwait(false);
                if (secret is null) return await CompleteAsync(Failure(ArchiveBuildFailureCode.SecretUnavailable, "Secure Secret material is unavailable for this execution."), false).ConfigureAwait(false);
            }

            try
            {
                await writer.WriteAsync(new ArchiveWriteRequest(input, request.Archive.Candidate.Unit.ArchiveSpec, request.Archive.Capability, secret), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return await CompleteAsync(Failure(ArchiveBuildFailureCode.WriterFailed, SafeMessage("Archive writer failed.", ex)), false).ConfigureAwait(false); }
            finally { secret?.Dispose(); secret = null; }

            var verification = await verifier.VerifyAsync(input.Workspace.PartialArtifactPath, request.Archive.Candidate.GeneratedMetadata.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (!verification.FormatTestPassed) return await CompleteAsync(Failure(ArchiveBuildFailureCode.FormatTestFailed, "Archive format-level test failed."), false).ConfigureAwait(false);
            if (!EntrySetMatches(request.Archive.Candidate, verification.Entries)) return await CompleteAsync(Failure(ArchiveBuildFailureCode.EntrySetMismatch, "Archive entry path/kind set differs from Candidate payload plus manifest."), false).ConfigureAwait(false);

            var parsed = manifestCodec.ReadAndValidate(verification.ManifestBytes);
            if (!parsed.IsValid)
            {
                diagnostics.AddRange(parsed.Diagnostics);
                if (diagnostics.All(x => x.Code != ArchiveBuildFailureCode.ManifestInvalid)) diagnostics.Add(new(ArchiveBuildFailureCode.ManifestInvalid, "Archived manifest is invalid."));
                return await CompleteAsync(new(null, [.. diagnostics]), false).ConfigureAwait(false);
            }
            var expected = manifestCodec.ReadAndValidate(manifestBytes).Manifest!;
            if (!verification.ManifestBytes.Span.SequenceEqual(manifestBytes.Span) || !ManifestMatches(parsed.Manifest!, expected)) return await CompleteAsync(Failure(ArchiveBuildFailureCode.ManifestMismatch, "Archived manifest does not match the build Candidate."), false).ConfigureAwait(false);
            if (verification.Length < 0) return await CompleteAsync(Failure(ArchiveBuildFailureCode.IntegrityComputationFailed, "Archive length is invalid."), false).ConfigureAwait(false);

            var version = ArchiveVersion.Prepare(request.ArchiveVersionId, expected.PlanId, expected.ArchiveUnitId,
                request.Archive.Candidate.Unit.ArchiveSpec.Format, request.ArchiveSpecFingerprint)
                .Verify(verification.Sha256, verification.Length);
            preservePartial = true;
            return await CompleteAsync(new(new VerifiedArchiveArtifact(input.Workspace.PartialArtifactPath, version, expected), [.. diagnostics]), true).ConfigureAwait(false);
        }
        catch (ArchiveMaterializationException ex)
        {
            return await CompleteAsync(Failure(ex.Code, ex.Message, ex.ArchivePath), false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add(new(ArchiveBuildFailureCode.Cancelled, "Archive build was cancelled."));
            return await CompleteAsync(new(null, [.. diagnostics]), false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteAsync(Failure(ArchiveBuildFailureCode.IntegrityComputationFailed, SafeMessage("Archive verification failed.", ex)), false).ConfigureAwait(false);
        }
        finally
        {
            secret?.Dispose();
            // 每条返回路径先调用 CompleteAsync；finally 只兜底 Secret lease。
        }

        ArchiveBuildResult Failure(ArchiveBuildFailureCode code, string message, RelativePath? path = null)
        {
            diagnostics.Add(new(code, message, path));
            return new(null, [.. diagnostics]);
        }

        async Task<ArchiveBuildResult> CompleteAsync(ArchiveBuildResult result, bool keepPartial)
        {
            preservePartial = keepPartial;
            if (input is null) return result;
            try { await input.Workspace.CleanupAsync(preservePartial, CancellationToken.None).ConfigureAwait(false); }
            catch { diagnostics.Add(new(ArchiveBuildFailureCode.CleanupFailed, "Private staging/partial cleanup failed.", IsCleanupWarning: true)); }
            await input.Workspace.DisposeAsync().ConfigureAwait(false);
            input = null;
            return result with { Diagnostics = [.. diagnostics] };
        }
    }

    private static bool EntrySetMatches(CandidateArchive candidate, IEnumerable<ArchiveArtifactEntry> actual)
    {
        var expected = candidate.Entries.Select(x => new ArchiveArtifactEntry(x.ArchivePath, x.Kind)).OrderBy(Key, StringComparer.Ordinal);
        return expected.SequenceEqual(actual.OrderBy(Key, StringComparer.Ordinal));
        static string Key(ArchiveArtifactEntry x) => $"{x.Path.Value}\0{(int)x.Kind}";
    }

    private static bool ManifestMatches(ArchiveManifestV1 actual, ArchiveManifestV1 expected) =>
        actual.SchemaVersion == expected.SchemaVersion && actual.ArchiveSemanticsVersion == expected.ArchiveSemanticsVersion
        && actual.PlanId == expected.PlanId && actual.SourceId == expected.SourceId && actual.ArchiveUnitId == expected.ArchiveUnitId
        && actual.UnitLogicalPath == expected.UnitLogicalPath && actual.ArchiveSpec == expected.ArchiveSpec
        && actual.Entries.SequenceEqual(expected.Entries);

    // Adapter exception details may contain command/process data; Application only emits a fixed safe summary plus exception type.
    private static string SafeMessage(string prefix, Exception exception) => $"{prefix} ({exception.GetType().Name})";
}
