using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupChecksums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "checksum",
                table: "backups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checksum_algorithm",
                table: "backups",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_checksum",
                table: "backups",
                sql: "(checksum IS NULL) = (checksum_algorithm IS NULL) AND (checksum IS NULL OR (status = 'completed' AND length(checksum) = 64))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_checksum_algorithm",
                table: "backups",
                sql: "checksum_algorithm IS NULL OR checksum_algorithm IN ('sha256')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_checksum",
                table: "backups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_checksum_algorithm",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "checksum",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "checksum_algorithm",
                table: "backups");
        }
    }
}
