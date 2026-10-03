using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRestoreDatabaseJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_backup_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_backup_id",
                table: "jobs",
                sql: "(type IN ('backup_database', 'restore_database') AND backup_id IS NOT NULL) OR (type NOT IN ('backup_database', 'restore_database') AND backup_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs",
                sql: "(type IN ('create_database', 'delete_database', 'backup_database', 'restore_database') AND database_id IS NOT NULL) OR (type NOT IN ('create_database', 'delete_database', 'backup_database', 'restore_database') AND database_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database', 'backup_database', 'restore_database')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore jobs cannot be expressed in the previous schema.
            migrationBuilder.Sql("DELETE FROM jobs WHERE type = 'restore_database';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_backup_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

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
        }
    }
}
