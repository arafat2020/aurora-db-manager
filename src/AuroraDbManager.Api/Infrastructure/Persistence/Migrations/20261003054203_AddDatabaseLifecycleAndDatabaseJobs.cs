using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseLifecycleAndDatabaseJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "database_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "databases",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_message",
                table: "databases",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "databases",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "creating");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_database_id_unfinished",
                table: "jobs",
                column: "database_id",
                unique: true,
                filter: "status IN ('pending', 'running') AND database_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs",
                columns: new[] { "instance_id", "type" },
                unique: true,
                filter: "status IN ('pending', 'running') AND database_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs",
                sql: "(type IN ('create_database', 'delete_database') AND database_id IS NOT NULL) OR (type NOT IN ('create_database', 'delete_database') AND database_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_databases_status",
                table: "databases",
                sql: "status IN ('creating', 'ready', 'deleting', 'failed')");

            // Databases stored before this migration are metadata only: nothing was created in an
            // engine for them. They are now 'creating', and each gets the job that creates it,
            // which the worker picks up at the next start.
            migrationBuilder.Sql(
                """
                INSERT INTO jobs (id, type, status, instance_id, database_id, attempt, max_attempts, created_at, updated_at)
                SELECT gen_random_uuid(), 'create_database', 'pending', instance_id, id, 0, 3, now(), now()
                FROM databases;
                """);

            // The default only served the rows above; the application always supplies a status.
            migrationBuilder.Sql("ALTER TABLE databases ALTER COLUMN status DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Database jobs cannot be expressed in the previous schema.
            migrationBuilder.Sql("DELETE FROM jobs WHERE database_id IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "ux_jobs_database_id_unfinished",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_database_id",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_databases_status",
                table: "databases");

            migrationBuilder.DropColumn(
                name: "database_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "databases");

            migrationBuilder.DropColumn(
                name: "error_message",
                table: "databases");

            migrationBuilder.DropColumn(
                name: "status",
                table: "databases");

            migrationBuilder.CreateIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs",
                columns: new[] { "instance_id", "type" },
                unique: true,
                filter: "status IN ('pending', 'running')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance')");
        }
    }
}
