using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StowCrate.Infrastructure.Persistence.ConfigDb.Migrations;

/// <inheritdoc />
public partial class AddPublishIntentHistoryRequirementV2 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        /// <inheritdoc />
            migrationBuilder.DropCheckConstraint(
                name: "CK_PublishIntent_StageFacts",
                table: "PublishIntent");

            migrationBuilder.AddColumn<string>(
                name: "HistoryCaptureRequirement",
                table: "PublishIntent",
                type: "TEXT",
                nullable: false,
                defaultValue: "UNKNOWN_LEGACY");

            // v1 incomplete old-Current intent cannot reveal its original effective History policy.
            // First backup has no old Current and is unambiguously NotRequired.
            migrationBuilder.Sql("UPDATE PublishIntent SET HistoryCaptureRequirement='NOT_REQUIRED' WHERE OldArchiveVersionId IS NULL;");
            migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=2 WHERE SingletonKey=1;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PublishIntent_HistoryRequirement",
                table: "PublishIntent",
                sql: "HistoryCaptureRequirement IN ('REQUIRED','NOT_REQUIRED','UNKNOWN_LEGACY')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PublishIntent_StageFacts",
                table: "PublishIntent",
                sql: "(Stage='PREPARED' AND CurrentPublishedAtUtcMs IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (Stage='HISTORY_CAPTURED' AND HistoryCaptureRequirement IN ('REQUIRED','UNKNOWN_LEGACY') AND CurrentPublishedAtUtcMs IS NULL AND OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL) OR (Stage IN ('CURRENT_PUBLISHED','METADATA_COMMITTED') AND CurrentPublishedAtUtcMs IS NOT NULL AND ((HistoryCaptureRequirement='REQUIRED' AND OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL) OR (HistoryCaptureRequirement='NOT_REQUIRED' AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (HistoryCaptureRequirement='UNKNOWN_LEGACY' AND ((HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL)))))");
    }

        /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
            migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=1 WHERE SingletonKey=1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_PublishIntent_HistoryRequirement",
                table: "PublishIntent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PublishIntent_StageFacts",
                table: "PublishIntent");

            migrationBuilder.DropColumn(
                name: "HistoryCaptureRequirement",
                table: "PublishIntent");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PublishIntent_StageFacts",
                table: "PublishIntent",
                sql: "(Stage='PREPARED' AND CurrentPublishedAtUtcMs IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (Stage='HISTORY_CAPTURED' AND CurrentPublishedAtUtcMs IS NULL AND OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL) OR (Stage IN ('CURRENT_PUBLISHED','METADATA_COMMITTED') AND CurrentPublishedAtUtcMs IS NOT NULL AND ((OldArchiveVersionId IS NULL AND HistoryRelativePath IS NULL AND HistoryVerifiedIntegritySha256 IS NULL) OR (OldArchiveVersionId IS NOT NULL AND HistoryRelativePath IS NOT NULL AND HistoryVerifiedIntegritySha256 IS NOT NULL)))");
    }
}
