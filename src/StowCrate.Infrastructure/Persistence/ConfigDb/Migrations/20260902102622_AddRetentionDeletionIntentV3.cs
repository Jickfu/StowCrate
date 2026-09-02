using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace StowCrate.Infrastructure.Persistence.ConfigDb.Migrations;

/// <inheritdoc />
public partial class AddRetentionDeletionIntentV3 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RetentionDeletionIntent",
            columns: table => new
            {
                ArchiveVersionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                PlanId = table.Column<byte[]>(type: "BLOB", nullable: false),
                ArchiveUnitId = table.Column<byte[]>(type: "BLOB", nullable: false),
                SelectionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                Stage = table.Column<string>(type: "TEXT", nullable: false),
                HistoryRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                ExpectedIntegritySha256 = table.Column<byte[]>(type: "BLOB", nullable: false),
                ExpectedLength = table.Column<long>(type: "INTEGER", nullable: false),
                RetentionSemanticsVersion = table.Column<long>(type: "INTEGER", nullable: false),
                KeepLastVersionsCount = table.Column<long>(type: "INTEGER", nullable: false),
                SelectedAtUtcMs = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAtUtcMs = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RetentionDeletionIntent", x => x.ArchiveVersionId);
                table.CheckConstraint("CK_RetentionDeletionIntent_Digest", "length(ExpectedIntegritySha256)=32");
                table.CheckConstraint("CK_RetentionDeletionIntent_Facts", "ExpectedLength>=0 AND RetentionSemanticsVersion=1 AND KeepLastVersionsCount>=1 AND ((Stage='PREPARED' AND CompletedAtUtcMs IS NULL) OR (Stage='COMPLETED' AND CompletedAtUtcMs IS NOT NULL))");
                table.CheckConstraint("CK_RetentionDeletionIntent_Ids", "length(ArchiveVersionId)=16 AND length(PlanId)=16 AND length(ArchiveUnitId)=16 AND length(SelectionId)=16");
                table.CheckConstraint("CK_RetentionDeletionIntent_Stage", "Stage IN ('PREPARED','COMPLETED')");
                table.ForeignKey(
                    name: "FK_RetentionDeletionIntent_ArchiveVersion_PlanId_ArchiveUnitId_ArchiveVersionId",
                    columns: x => new { x.PlanId, x.ArchiveUnitId, x.ArchiveVersionId },
                    principalTable: "ArchiveVersion",
                    principalColumns: new[] { "PlanId", "ArchiveUnitId", "ArchiveVersionId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RetentionDeletionIntent_PlanId_ArchiveUnitId_ArchiveVersionId",
            table: "RetentionDeletionIntent",
            columns: new[] { "PlanId", "ArchiveUnitId", "ArchiveVersionId" });

        migrationBuilder.CreateIndex(
            name: "IX_RetentionDeletionIntent_SelectionId",
            table: "RetentionDeletionIntent",
            column: "SelectionId");

        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=3 WHERE SingletonKey=1;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=2 WHERE SingletonKey=1;");
        migrationBuilder.DropTable(
            name: "RetentionDeletionIntent");
    }
}
