using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S7_DeptSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "primary_department_alerts",
                table: "staff",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "alert_group",
                table: "departments",
                type: "text",
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<bool>(
                name: "assign_primary_only",
                table: "departments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "disable_auto_claim",
                table: "departments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "disable_reopen_auto_assign",
                table: "departments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "departments",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "primary_department_alerts",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "alert_group",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "assign_primary_only",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "disable_auto_claim",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "disable_reopen_auto_assign",
                table: "departments");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "departments");
        }
    }
}
