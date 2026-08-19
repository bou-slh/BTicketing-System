using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S7_ScheduleEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "schedules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_holiday",
                table: "schedule_entry",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                table: "schedules");

            migrationBuilder.DropColumn(
                name: "is_holiday",
                table: "schedule_entry");
        }
    }
}
