using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    engine = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cpu = table.Column<int>(type: "integer", nullable: false),
                    memory_mb = table.Column<int>(type: "integer", nullable: false),
                    storage_gb = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instances", x => x.id);
                    table.CheckConstraint("ck_instances_cpu", "cpu > 0");
                    table.CheckConstraint("ck_instances_engine", "engine IN ('postgres', 'mysql')");
                    table.CheckConstraint("ck_instances_memory_mb", "memory_mb > 0");
                    table.CheckConstraint("ck_instances_status", "status IN ('provisioning', 'running', 'stopped', 'failed')");
                    table.CheckConstraint("ck_instances_storage_gb", "storage_gb > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_instances_created_at",
                table: "instances",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instances");
        }
    }
}
