using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S7_EmailAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "o_auth_client_id",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "o_auth_client_secret_protected",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_protected",
                table: "email_channel",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "o_auth_client_id",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_client_secret_protected",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "password_protected",
                table: "email_channel");
        }
    }
}
