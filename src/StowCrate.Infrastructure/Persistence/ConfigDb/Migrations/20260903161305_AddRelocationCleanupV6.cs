using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StowCrate.Infrastructure.Persistence.ConfigDb.Migrations;

/// <inheritdoc />
public partial class AddRelocationCleanupV6 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=6 WHERE SingletonKey=1;");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent",
            sql: "Stage IN ('PREPARED','TARGETS_DURABLE','METADATA_COMMITTED','COMPLETED')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // v5 不能读取 cleanup 协议；先拒绝降级，禁止丢失 absence 语义。
        migrationBuilder.Sql("CREATE TEMP TABLE relocation_cleanup_downgrade_guard(Value INTEGER CHECK(Value=0)); INSERT INTO relocation_cleanup_downgrade_guard SELECT COUNT(*) FROM StorageRelocationIntent WHERE Stage='COMPLETED' OR json_extract(CAST(ProgressPayload AS TEXT),'$.Version')=3; DROP TABLE relocation_cleanup_downgrade_guard;");
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=5 WHERE SingletonKey=1;");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent",
            sql: "Stage IN ('PREPARED','TARGETS_DURABLE','METADATA_COMMITTED')");
    }
}
