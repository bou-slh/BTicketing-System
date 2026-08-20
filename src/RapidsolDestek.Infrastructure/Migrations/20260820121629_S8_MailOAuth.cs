using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S8_MailOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "o_auth_access_token_expires_at",
                table: "email_channel",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "o_auth_access_token_protected",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "o_auth_consent_account",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "o_auth_consent_at",
                table: "email_channel",
                type: "timestamp with time zone",
                nullable: true);

            // Enums are persisted as strings (AppDbContext.ConfigureConventions), so the
            // backfill for existing rows must be a real enum name — EF's generated ""
            // would not round-trip. Microsoft is the entity default.
            migrationBuilder.AddColumn<string>(
                name: "o_auth_provider",
                table: "email_channel",
                type: "text",
                nullable: false,
                defaultValue: "Microsoft");

            migrationBuilder.AddColumn<string>(
                name: "o_auth_refresh_token_protected",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "o_auth_scopes",
                table: "email_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "o_auth_tenant",
                table: "email_channel",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "o_auth_access_token_expires_at",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_access_token_protected",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_consent_account",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_consent_at",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_provider",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_refresh_token_protected",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_scopes",
                table: "email_channel");

            migrationBuilder.DropColumn(
                name: "o_auth_tenant",
                table: "email_channel");
        }
    }
}
