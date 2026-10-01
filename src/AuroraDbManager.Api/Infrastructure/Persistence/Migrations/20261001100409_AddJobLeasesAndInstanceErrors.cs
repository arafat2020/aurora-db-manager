using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLeasesAndInstanceErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "lease_expires_at",
                table: "jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "instances",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "error_message",
                table: "instances",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs",
                columns: new[] { "instance_id", "type" },
                unique: true,
                filter: "status IN ('pending', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_jobs_instance_id_type_unfinished",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "instances");

            migrationBuilder.DropColumn(
                name: "error_message",
                table: "instances");
        }
    }
}
