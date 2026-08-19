using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S7_QueueBuilder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conditions",
                table: "saved_queues",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_system",
                table: "saved_queues",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conditions",
                table: "saved_queues");

            migrationBuilder.DropColumn(
                name: "is_system",
                table: "saved_queues");
        }
    }
}
