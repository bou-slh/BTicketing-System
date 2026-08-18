using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S6_StaffPrefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_refresh_minutes",
                table: "staff",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "default_queue",
                table: "staff",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "page_size",
                table: "staff",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "password_changed_at",
                table: "staff",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "thread_order_newest_first",
                table: "staff",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "two_factor_method",
                table: "staff",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "use24hour_time",
                table: "staff",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_refresh_minutes",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "default_queue",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "page_size",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "password_changed_at",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "thread_order_newest_first",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "two_factor_method",
                table: "staff");

            migrationBuilder.DropColumn(
                name: "use24hour_time",
                table: "staff");
        }
    }
}
