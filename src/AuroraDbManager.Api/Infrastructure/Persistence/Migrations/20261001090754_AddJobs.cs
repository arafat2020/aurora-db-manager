using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempt", "attempt >= 0 AND attempt <= max_attempts");
                    table.CheckConstraint("ck_jobs_max_attempts", "max_attempts > 0");
                    table.CheckConstraint("ck_jobs_status", "status IN ('pending', 'running', 'completed', 'failed')");
                    table.CheckConstraint("ck_jobs_type", "type IN ('provision_instance')");
                    table.ForeignKey(
                        name: "fk_jobs_instances_instance_id",
                        column: x => x.instance_id,
                        principalTable: "instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_instance_id",
                table: "jobs",
                column: "instance_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status",
                table: "jobs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs");
        }
    }
}
