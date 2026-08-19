using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S7_StaffPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auth_backend",
                table: "staff",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<List<string>>(
                name: "permissions",
                table: "staff",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "require_password_change",
                table: "staff",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "use_primary_role_on_assigned",
                table: "staff",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auth_backend",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "permissions",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "require_password_change",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "use_primary_role_on_assigned",
                table: "staff");
        }
    }
}
