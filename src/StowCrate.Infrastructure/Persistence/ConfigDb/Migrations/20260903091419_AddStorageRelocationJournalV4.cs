using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StowCrate.Infrastructure.Persistence.ConfigDb.Migrations;

/// <inheritdoc />
public partial class AddStorageRelocationJournalV4 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=4 WHERE SingletonKey=1;");
        migrationBuilder.CreateTable(
            name: "StorageRelocationIntent",
            columns: table => new
            {
                TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                PlanId = table.Column<byte[]>(type: "BLOB", nullable: false),
                DeviceId = table.Column<byte[]>(type: "BLOB", nullable: false),
                ProtocolVersion = table.Column<long>(type: "INTEGER", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false),
                Stage = table.Column<string>(type: "TEXT", nullable: false),
                ManifestPayload = table.Column<byte[]>(type: "BLOB", nullable: false),
                ManifestSha256 = table.Column<byte[]>(type: "BLOB", nullable: false),
                ProgressPayload = table.Column<byte[]>(type: "BLOB", nullable: false),
                ProgressSha256 = table.Column<byte[]>(type: "BLOB", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorageRelocationIntent", x => x.TransactionId);
                table.CheckConstraint("CK_Relocation_Ids", "length(TransactionId)=16 AND length(PlanId)=16 AND length(DeviceId)=16");
                table.CheckConstraint("CK_Relocation_Payload", "length(ManifestPayload)>0 AND length(ProgressPayload)>0 AND length(ManifestSha256)=32 AND length(ProgressSha256)=32");
                table.CheckConstraint("CK_Relocation_Protocol", "ProtocolVersion=1 AND Revision>=1");
                table.CheckConstraint("CK_Relocation_Stage", "Stage IN ('PREPARED','TARGETS_DURABLE')");
                table.ForeignKey(
                    name: "FK_StorageRelocationIntent_PlanRegistration_PlanId",
                    column: x => x.PlanId,
                    principalTable: "PlanRegistration",
                    principalColumn: "PlanId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "StorageRelocationRootReservation",
            columns: table => new
            {
                TransactionId = table.Column<byte[]>(type: "BLOB", nullable: false),
                Slot = table.Column<string>(type: "TEXT", nullable: false),
                CanonicalPath = table.Column<string>(type: "TEXT", nullable: false),
                ComparisonKey = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorageRelocationRootReservation", x => new { x.TransactionId, x.Slot });
                table.CheckConstraint("CK_RelocationRoot_Path", "length(CanonicalPath)>0 AND length(ComparisonKey)>0");
                table.CheckConstraint("CK_RelocationRoot_Slot", "Slot IN ('CURRENT_OLD','CURRENT_NEW','HISTORY_OLD','HISTORY_NEW')");
                table.ForeignKey(
                    name: "FK_StorageRelocationRootReservation_StorageRelocationIntent_TransactionId",
                    column: x => x.TransactionId,
                    principalTable: "StorageRelocationIntent",
                    principalColumn: "TransactionId",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_StorageRelocationIntent_PlanId",
            table: "StorageRelocationIntent",
            column: "PlanId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=3 WHERE SingletonKey=1;");
        migrationBuilder.DropTable(
            name: "StorageRelocationRootReservation");

        migrationBuilder.DropTable(
            name: "StorageRelocationIntent");
    }
}
