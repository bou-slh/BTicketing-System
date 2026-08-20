using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S8_Inbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "message_id",
                table: "email_outbounds",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "email_inbounds",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email_channel_id = table.Column<int>(type: "integer", nullable: false),
                    email_account_id = table.Column<int>(type: "integer", nullable: false),
                    uid = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    from_address = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    ticket_id = table.Column<int>(type: "integer", nullable: true),
                    thread_entry_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_inbounds", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbounds_message_id",
                table: "email_outbounds",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_inbounds_email_channel_id_uid",
                table: "email_inbounds",
                columns: new[] { "email_channel_id", "uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_inbounds_message_id",
                table: "email_inbounds",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_inbounds_ticket_id",
                table: "email_inbounds",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_inbounds");

            migrationBuilder.DropIndex(
                name: "ix_email_outbounds_message_id",
                table: "email_outbounds");

            migrationBuilder.DropColumn(
                name: "message_id",
                table: "email_outbounds");
        }
    }
}
