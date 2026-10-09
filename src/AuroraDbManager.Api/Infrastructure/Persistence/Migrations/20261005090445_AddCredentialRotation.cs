using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.AddColumn<DateTime>(
                name: "delivery_consumed_at",
                table: "instance_secrets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "delivery_expires_at",
                table: "instance_secrets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "delivery_job_id",
                table: "instance_secrets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "protected_pending_admin_password",
                table: "instance_secrets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database', 'backup_database', 'restore_database', 'rotate_credential')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "delivery_consumed_at",
                table: "instance_secrets");

            migrationBuilder.DropColumn(
                name: "delivery_expires_at",
                table: "instance_secrets");

            migrationBuilder.DropColumn(
                name: "delivery_job_id",
                table: "instance_secrets");

            migrationBuilder.DropColumn(
                name: "protected_pending_admin_password",
                table: "instance_secrets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_jobs_type",
                table: "jobs",
                sql: "type IN ('provision_instance', 'create_database', 'delete_database', 'backup_database', 'restore_database')");
        }
    }
}
