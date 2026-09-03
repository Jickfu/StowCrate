using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StowCrate.Infrastructure.Persistence.ConfigDb.Migrations;

/// <inheritdoc />
public partial class AddRelocationCommitV5 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=5 WHERE SingletonKey=1;");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent");

        migrationBuilder.AddColumn<byte[]>(
            name: "ConfigurationPayload",
            table: "StorageRelocationIntent",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddColumn<byte[]>(
            name: "ConfigurationSha256",
            table: "StorageRelocationIntent",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Relocation_Configuration",
            table: "StorageRelocationIntent",
            sql: "(ConfigurationPayload IS NULL AND ConfigurationSha256 IS NULL) OR (ConfigurationPayload IS NOT NULL AND ConfigurationSha256 IS NOT NULL AND length(ConfigurationPayload)>0 AND length(ConfigurationSha256)=32)");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent",
            sql: "Stage IN ('PREPARED','TARGETS_DURABLE','METADATA_COMMITTED')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // 不允许回退时丢失新协议配置授权或已提交事务。
        migrationBuilder.Sql("CREATE TEMP TABLE relocation_downgrade_guard(Value INTEGER CHECK(Value=0)); INSERT INTO relocation_downgrade_guard SELECT COUNT(*) FROM StorageRelocationIntent WHERE ConfigurationPayload IS NOT NULL OR Stage='METADATA_COMMITTED'; DROP TABLE relocation_downgrade_guard;");
        migrationBuilder.Sql("UPDATE DatabaseMetadata SET SchemaVersion=4 WHERE SingletonKey=1;");
        migrationBuilder.DropCheckConstraint(
            name: "CK_Relocation_Configuration",
            table: "StorageRelocationIntent");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent");

        migrationBuilder.DropColumn(
            name: "ConfigurationPayload",
            table: "StorageRelocationIntent");

        migrationBuilder.DropColumn(
            name: "ConfigurationSha256",
            table: "StorageRelocationIntent");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Relocation_Stage",
            table: "StorageRelocationIntent",
            sql: "Stage IN ('PREPARED','TARGETS_DURABLE')");
    }
}
