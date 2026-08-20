using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S8_Tokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "include_attachments",
                table: "email_outbounds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_automated",
                table: "email_outbounds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Every row written before this migration WAS stamped Auto-Submitted by
            // the slice-4 transport, so the honest backfill is true — the CLR default
            // (false) would retroactively claim old notifications were human-authored.
            // New rows get their value from the entity, whose initializer is true.
            migrationBuilder.Sql("UPDATE email_outbounds SET is_automated = TRUE;");

            migrationBuilder.CreateTable(
                name: "mail_tokens",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    jti = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_tokens", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_tokens_jti",
                table: "mail_tokens",
                column: "jti",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_tokens_user_id_purpose",
                table: "mail_tokens",
                columns: new[] { "user_id", "purpose" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_tokens");

            migrationBuilder.DropColumn(
                name: "include_attachments",
                table: "email_outbounds");

            migrationBuilder.DropColumn(
                name: "is_automated",
                table: "email_outbounds");
        }
    }
}
