using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddS3BackupStorageType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_storage_type",
                table: "backups");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_storage_type",
                table: "backups",
                sql: "storage_type IN ('local', 's3')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_storage_type",
                table: "backups");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_storage_type",
                table: "backups",
                sql: "storage_type IN ('local')");
        }
    }
}
