using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "backup_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    database_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    storage_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backups", x => x.id);
                    table.CheckConstraint("ck_backups_artifact", "(status = 'completed') = (path IS NOT NULL AND size_bytes IS NOT NULL)");
                    table.CheckConstraint("ck_backups_size_bytes", "size_bytes IS NULL OR size_bytes > 0");
                    table.CheckConstraint("ck_backups_status", "status IN ('pending', 'running', 'completed', 'failed')");
                    table.CheckConstraint("ck_backups_storage_type", "storage_type IN ('local')");
                    table.ForeignKey(
                        name: "fk_backups_databases_database_id",
                        column: x => x.database_id,
                        principalTable: "databases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_backup_id",
                table: "jobs",
                sql: "(type = 'backup_database' AND backup_id IS NOT NULL) OR (type <> 'backup_database' AND backup_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs",
                sql: "(type IN ('create_database', 'delete_database', 'backup_database') AND database_id IS NOT NULL) OR (type NOT IN ('create_database', 'delete_database', 'backup_database') AND database_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database', 'backup_database')");

            migrationBuilder.CreateIndex(
                name: "ix_backups_database_id_created_at",
                table: "backups",
                columns: new[] { "database_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Backup jobs cannot be expressed in the previous schema.
            migrationBuilder.Sql("DELETE FROM jobs WHERE type = 'backup_database';");

            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_backup_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "backup_id",
                table: "jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs",
                sql: "(type IN ('create_database', 'delete_database') AND database_id IS NOT NULL) OR (type NOT IN ('create_database', 'delete_database') AND database_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database')");
        }
    }
}
