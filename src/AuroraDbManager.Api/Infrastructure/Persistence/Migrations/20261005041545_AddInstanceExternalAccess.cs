using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuroraDbManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceExternalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "external_access_enabled",
                table: "instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "external_port",
                table: "instances",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_instances_external_port",
                table: "instances",
                column: "external_port",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_instances_external_access",
                table: "instances",
                sql: "(external_access_enabled AND external_port IS NOT NULL) OR (NOT external_access_enabled AND external_port IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_instances_external_port",
                table: "instances",
                sql: "external_port IS NULL OR (external_port >= 1024 AND external_port <= 65535)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_instances_external_port",
                table: "instances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_instances_external_access",
                table: "instances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_instances_external_port",
                table: "instances");

            migrationBuilder.DropColumn(
                name: "external_access_enabled",
                table: "instances");

            migrationBuilder.DropColumn(
                name: "external_port",
                table: "instances");
        }
    }
}
