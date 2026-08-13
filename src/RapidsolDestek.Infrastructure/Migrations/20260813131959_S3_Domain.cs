using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RapidsolDestek.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class S3_Domain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    can_create_tickets = table.Column<bool>(type: "boolean", nullable: false),
                    can_trigger_jobs = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    actor_name = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<string>(type: "text", nullable: false),
                    object_label = table.Column<string>(type: "text", nullable: true),
                    data = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "banlist_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    address = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_banlist_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "drafts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    staff_id = table.Column<int>(type: "integer", nullable: false),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    extra = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_drafts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "edit_locks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    staff_id = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edit_locks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_template_sets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_template_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "filters",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    exec_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    match_all_rules = table.Column<bool>(type: "boolean", nullable: false),
                    stop_on_match = table.Column<bool>(type: "boolean", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    email_account_id = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filters", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "form_definitions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kb_categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kb_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_kb_categories_kb_categories_parent_id",
                        column: x => x.parent_id,
                        principalTable: "kb_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "list_definitions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    plural_name = table.Column<string>(type: "text", nullable: true),
                    sort_mode = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: true),
                    configuration = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_list_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    domain = table.Column<string>(type: "text", nullable: true),
                    manager_staff_id = table.Column<int>(type: "integer", nullable: true),
                    share_tickets_with_members = table.Column<bool>(type: "boolean", nullable: false),
                    cc_primary_contacts = table.Column<bool>(type: "boolean", nullable: false),
                    assign_to_manager = table.Column<bool>(type: "boolean", nullable: false),
                    address = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    website = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queue_columns",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    primary_path = table.Column<string>(type: "text", nullable: false),
                    secondary_path = table.Column<string>(type: "text", nullable: true),
                    decorator = table.Column<string>(type: "text", nullable: true),
                    truncate = table.Column<string>(type: "text", nullable: true),
                    annotations = table.Column<string>(type: "text", nullable: true),
                    conditions = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue_columns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queue_sort_options",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    root = table.Column<string>(type: "text", nullable: true),
                    columns = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queue_sort_options", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    permissions = table.Column<List<string>>(type: "text[]", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "saved_queues",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "text", nullable: false),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    root = table.Column<string>(type: "text", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    criteria = table.Column<string>(type: "text", nullable: true),
                    quick_filter = table.Column<string>(type: "text", nullable: true),
                    inherit_columns = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_queues", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_queues_saved_queues_parent_id",
                        column: x => x.parent_id,
                        principalTable: "saved_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "schedules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    timezone = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sequences",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    next = table.Column<long>(type: "bigint", nullable: false),
                    increment = table.Column<int>(type: "integer", nullable: false),
                    padding = table.Column<char>(type: "character(1)", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    @namespace = table.Column<string>(name: "namespace", type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "site_pages",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_site_pages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stored_files",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    backend = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_log_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    log = table.Column<string>(type: "text", nullable: false),
                    logger = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_log_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    lead_staff_id = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    no_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "thread_event_types",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_thread_event_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "threads",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    last_response_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_threads", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_priorities",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    urgency = table.Column<int>(type: "integer", nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_priorities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_statuses",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    allow_reopen = table.Column<bool>(type: "boolean", nullable: false),
                    reopen_status_id = table.Column<int>(type: "integer", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_statuses", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "email_template",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    set_id = table.Column<int>(type: "integer", nullable: false),
                    code_name = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_template", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_template_email_template_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "email_template_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "filter_action",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    filter_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    configuration = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filter_action", x => x.id);
                    table.ForeignKey(
                        name: "fk_filter_action_filters_filter_id",
                        column: x => x.filter_id,
                        principalTable: "filters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "filter_rule",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    filter_id = table.Column<int>(type: "integer", nullable: false),
                    what = table.Column<string>(type: "text", nullable: false),
                    how = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_filter_rule", x => x.id);
                    table.ForeignKey(
                        name: "fk_filter_rule_filters_filter_id",
                        column: x => x.filter_id,
                        principalTable: "filters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    form_definition_id = table.Column<int>(type: "integer", nullable: false),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_form_entries_form_definitions_form_definition_id",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "form_field",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    form_definition_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    configuration = table.Column<string>(type: "text", nullable: true),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    hint = table.Column<string>(type: "text", nullable: true),
                    required_for_agents = table.Column<bool>(type: "boolean", nullable: false),
                    required_for_users = table.Column<bool>(type: "boolean", nullable: false),
                    visible_to_agents = table.Column<bool>(type: "boolean", nullable: false),
                    visible_to_users = table.Column<bool>(type: "boolean", nullable: false),
                    is_disabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_field", x => x.id);
                    table.ForeignKey(
                        name: "fk_form_field_form_definitions_form_definition_id",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "faq_articles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    question = table.Column<string>(type: "text", nullable: false),
                    answer = table.Column<string>(type: "text", nullable: false),
                    keywords = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    helpful_yes = table.Column<int>(type: "integer", nullable: false),
                    helpful_no = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_faq_articles", x => x.id);
                    table.ForeignKey(
                        name: "fk_faq_articles_kb_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "kb_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "list_item",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    list_definition_id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    abbrev = table.Column<string>(type: "text", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    properties = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_list_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_list_item_list_definitions_list_definition_id",
                        column: x => x.list_definition_id,
                        principalTable: "list_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_queue_column",
                columns: table => new
                {
                    queue_id = table.Column<int>(type: "integer", nullable: false),
                    column_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: true),
                    width = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_queue_column", x => new { x.queue_id, x.column_id });
                    table.ForeignKey(
                        name: "fk_saved_queue_column_queue_columns_column_id",
                        column: x => x.column_id,
                        principalTable: "queue_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_queue_column_saved_queues_queue_id",
                        column: x => x.queue_id,
                        principalTable: "saved_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_queue_export_field",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    queue_id = table.Column<int>(type: "integer", nullable: false),
                    field_path = table.Column<string>(type: "text", nullable: false),
                    heading = table.Column<string>(type: "text", nullable: true),
                    sort = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_queue_export_field", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_queue_export_field_saved_queues_queue_id",
                        column: x => x.queue_id,
                        principalTable: "saved_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_queue_sort",
                columns: table => new
                {
                    queue_id = table.Column<int>(type: "integer", nullable: false),
                    sort_option_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_queue_sort", x => new { x.queue_id, x.sort_option_id });
                    table.ForeignKey(
                        name: "fk_saved_queue_sort_queue_sort_options_sort_option_id",
                        column: x => x.sort_option_id,
                        principalTable: "queue_sort_options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_queue_sort_saved_queues_queue_id",
                        column: x => x.queue_id,
                        principalTable: "saved_queues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedule_entry",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    schedule_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    repeats = table.Column<string>(type: "text", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    starts_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ends_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    stops_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    day = table.Column<int>(type: "integer", nullable: true),
                    week = table.Column<int>(type: "integer", nullable: true),
                    month = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_entry", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedule_entry_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sla_plans",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    grace_period_hours = table.Column<int>(type: "integer", nullable: false),
                    schedule_id = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    escalate_on_overdue = table.Column<bool>(type: "boolean", nullable: false),
                    disable_overdue_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    is_transient = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_sla_plans_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "schedules",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "attachments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    object_type = table.Column<string>(type: "text", nullable: false),
                    object_id = table.Column<int>(type: "integer", nullable: false),
                    file_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    inline = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_attachments_stored_files_file_id",
                        column: x => x.file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "thread_entries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    type = table.Column<string>(type: "text", nullable: false),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<int>(type: "integer", nullable: true),
                    poster = table.Column<string>(type: "text", nullable: false),
                    edited_by_staff_id = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    flags = table.Column<int>(type: "integer", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    recipients = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_thread_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_thread_entries_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "thread_events",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    event_type_id = table.Column<int>(type: "integer", nullable: false),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    team_id = table.Column<int>(type: "integer", nullable: true),
                    department_id = table.Column<int>(type: "integer", nullable: true),
                    help_topic_id = table.Column<int>(type: "integer", nullable: true),
                    data = table.Column<string>(type: "text", nullable: true),
                    username = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    annulled = table.Column<bool>(type: "boolean", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_thread_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_thread_events_thread_event_types_event_type_id",
                        column: x => x.event_type_id,
                        principalTable: "thread_event_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_thread_events_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "form_entry_value",
                columns: table => new
                {
                    form_entry_id = table.Column<int>(type: "integer", nullable: false),
                    form_field_id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "text", nullable: true),
                    value_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_form_entry_value", x => new { x.form_entry_id, x.form_field_id });
                    table.ForeignKey(
                        name: "fk_form_entry_value_form_entries_form_entry_id",
                        column: x => x.form_entry_id,
                        principalTable: "form_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_form_entry_value_form_field_form_field_id",
                        column: x => x.form_field_id,
                        principalTable: "form_field",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    path = table.Column<string>(type: "text", nullable: false),
                    sla_id = table.Column<int>(type: "integer", nullable: true),
                    schedule_id = table.Column<int>(type: "integer", nullable: true),
                    template_set_id = table.Column<int>(type: "integer", nullable: true),
                    email_account_id = table.Column<int>(type: "integer", nullable: true),
                    auto_response_email_account_id = table.Column<int>(type: "integer", nullable: true),
                    manager_staff_id = table.Column<int>(type: "integer", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    ticket_auto_response = table.Column<bool>(type: "boolean", nullable: false),
                    message_auto_response = table.Column<bool>(type: "boolean", nullable: false),
                    assign_members_only = table.Column<bool>(type: "boolean", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.ForeignKey(
                        name: "fk_departments_departments_parent_id",
                        column: x => x.parent_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_departments_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "schedules",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_departments_sla_plans_sla_id",
                        column: x => x.sla_id,
                        principalTable: "sla_plans",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "canned_responses",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    department_id = table.Column<int>(type: "integer", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    response = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_canned_responses", x => x.id);
                    table.ForeignKey(
                        name: "fk_canned_responses_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "email_accounts",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    address = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    department_id = table.Column<int>(type: "integer", nullable: true),
                    priority_id = table.Column<int>(type: "integer", nullable: true),
                    help_topic_id = table.Column<int>(type: "integer", nullable: true),
                    no_auto_response = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_accounts_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "help_topics",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    no_auto_response = table.Column<bool>(type: "boolean", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: true),
                    priority_id = table.Column<int>(type: "integer", nullable: true),
                    sla_id = table.Column<int>(type: "integer", nullable: true),
                    status_id = table.Column<int>(type: "integer", nullable: true),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    team_id = table.Column<int>(type: "integer", nullable: true),
                    site_page_id = table.Column<int>(type: "integer", nullable: true),
                    sequence_id = table.Column<int>(type: "integer", nullable: true),
                    number_format = table.Column<string>(type: "text", nullable: true),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_help_topics", x => x.id);
                    table.ForeignKey(
                        name: "fk_help_topics_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_help_topics_help_topics_parent_id",
                        column: x => x.parent_id,
                        principalTable: "help_topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    identity_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    username = table.Column<string>(type: "text", nullable: false),
                    first_name = table.Column<string>(type: "text", nullable: false),
                    last_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    phone_ext = table.Column<string>(type: "text", nullable: true),
                    mobile = table.Column<string>(type: "text", nullable: true),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true),
                    timezone = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    on_vacation = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_only = table.Column<bool>(type: "boolean", nullable: false),
                    default_signature_type = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_staff_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "email_channel",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email_account_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    auth_kind = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    encryption = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    credential_ref = table.Column<string>(type: "text", nullable: true),
                    folder = table.Column<string>(type: "text", nullable: true),
                    fetch_frequency_minutes = table.Column<int>(type: "integer", nullable: false),
                    fetch_max = table.Column<int>(type: "integer", nullable: false),
                    post_fetch = table.Column<string>(type: "text", nullable: false),
                    archive_folder = table.Column<string>(type: "text", nullable: true),
                    allow_spoofing = table.Column<bool>(type: "boolean", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    last_error_message = table.Column<string>(type: "text", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_channel", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_channel_email_accounts_email_account_id",
                        column: x => x.email_account_id,
                        principalTable: "email_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "faq_article_topic",
                columns: table => new
                {
                    faq_article_id = table.Column<int>(type: "integer", nullable: false),
                    help_topic_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_faq_article_topic", x => new { x.faq_article_id, x.help_topic_id });
                    table.ForeignKey(
                        name: "fk_faq_article_topic_faq_articles_faq_article_id",
                        column: x => x.faq_article_id,
                        principalTable: "faq_articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_faq_article_topic_help_topics_help_topic_id",
                        column: x => x.help_topic_id,
                        principalTable: "help_topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "help_topic_form",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    help_topic_id = table.Column<int>(type: "integer", nullable: false),
                    form_definition_id = table.Column<int>(type: "integer", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    extra = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_help_topic_form", x => x.id);
                    table.ForeignKey(
                        name: "fk_help_topic_form_form_definitions_form_definition_id",
                        column: x => x.form_definition_id,
                        principalTable: "form_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_help_topic_form_help_topics_help_topic_id",
                        column: x => x.help_topic_id,
                        principalTable: "help_topics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_department_access",
                columns: table => new
                {
                    staff_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    alerts_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_department_access", x => new { x.staff_id, x.department_id });
                    table.ForeignKey(
                        name: "fk_staff_department_access_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_staff_department_access_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_staff_department_access_staff_staff_id",
                        column: x => x.staff_id,
                        principalTable: "staff",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "team_member",
                columns: table => new
                {
                    team_id = table.Column<int>(type: "integer", nullable: false),
                    staff_id = table.Column<int>(type: "integer", nullable: false),
                    alerts_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_member", x => new { x.team_id, x.staff_id });
                    table.ForeignKey(
                        name: "fk_team_member_staff_staff_id",
                        column: x => x.staff_id,
                        principalTable: "staff",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_member_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "effort_proposals",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    revision_no = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    proposed_by_staff_id = table.Column<int>(type: "integer", nullable: false),
                    decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_effort_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_items",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    ticket_id = table.Column<int>(type: "integer", nullable: true),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    team_id = table.Column<int>(type: "integer", nullable: true),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    lock_id = table.Column<int>(type: "integer", nullable: true),
                    due_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_overdue = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_items_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_items_staff_staff_id",
                        column: x => x.staff_id,
                        principalTable: "staff",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_task_items_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_task_items_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "thread_collaborators",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_thread_collaborators", x => x.id);
                    table.ForeignKey(
                        name: "fk_thread_collaborators_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    user_email_id = table.Column<int>(type: "integer", nullable: true),
                    status_id = table.Column<int>(type: "integer", nullable: false),
                    department_id = table.Column<int>(type: "integer", nullable: false),
                    priority_id = table.Column<int>(type: "integer", nullable: true),
                    sla_id = table.Column<int>(type: "integer", nullable: true),
                    help_topic_id = table.Column<int>(type: "integer", nullable: true),
                    staff_id = table.Column<int>(type: "integer", nullable: true),
                    team_id = table.Column<int>(type: "integer", nullable: true),
                    email_account_id = table.Column<int>(type: "integer", nullable: true),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    lock_id = table.Column<int>(type: "integer", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_extra = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    is_overdue = table.Column<bool>(type: "boolean", nullable: false),
                    is_answered = table.Column<bool>(type: "boolean", nullable: false),
                    due_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    estimated_due_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reopened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_update_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_tickets_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_help_topics_help_topic_id",
                        column: x => x.help_topic_id,
                        principalTable: "help_topics",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_sla_plans_sla_id",
                        column: x => x.sla_id,
                        principalTable: "sla_plans",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_staff_staff_id",
                        column: x => x.staff_id,
                        principalTable: "staff",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_teams_team_id",
                        column: x => x.team_id,
                        principalTable: "teams",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_ticket_priorities_priority_id",
                        column: x => x.priority_id,
                        principalTable: "ticket_priorities",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_tickets_ticket_statuses_status_id",
                        column: x => x.status_id,
                        principalTable: "ticket_statuses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_tickets_parent_id",
                        column: x => x.parent_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "user_emails",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_emails", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<int>(type: "integer", nullable: true),
                    default_email_id = table.Column<int>(type: "integer", nullable: true),
                    identity_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    is_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_users_user_emails_default_email_id",
                        column: x => x.default_email_id,
                        principalTable: "user_emails",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key",
                table: "api_keys",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_file_id",
                table: "attachments",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_attachments_object_type_object_id_file_id",
                table: "attachments",
                columns: new[] { "object_type", "object_id", "file_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_object_type_object_id",
                table: "audit_events",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_banlist_entries_address",
                table: "banlist_entries",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_department_id",
                table: "canned_responses",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_is_enabled",
                table: "canned_responses",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_canned_responses_title",
                table: "canned_responses",
                column: "title",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_name_parent_id",
                table: "departments",
                columns: new[] { "name", "parent_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_parent_id",
                table: "departments",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_schedule_id",
                table: "departments",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_sla_id",
                table: "departments",
                column: "sla_id");

            migrationBuilder.CreateIndex(
                name: "ix_drafts_staff_id_namespace",
                table: "drafts",
                columns: new[] { "staff_id", "namespace" });

            migrationBuilder.CreateIndex(
                name: "ix_effort_proposals_ticket_id_revision_no",
                table: "effort_proposals",
                columns: new[] { "ticket_id", "revision_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_accounts_address",
                table: "email_accounts",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_accounts_department_id",
                table: "email_accounts",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_email_channel_email_account_id_kind",
                table: "email_channel",
                columns: new[] { "email_account_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_email_template_set_id_code_name",
                table: "email_template",
                columns: new[] { "set_id", "code_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_faq_article_topic_help_topic_id",
                table: "faq_article_topic",
                column: "help_topic_id");

            migrationBuilder.CreateIndex(
                name: "ix_faq_articles_category_id",
                table: "faq_articles",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_faq_articles_is_published",
                table: "faq_articles",
                column: "is_published");

            migrationBuilder.CreateIndex(
                name: "ix_faq_articles_question",
                table: "faq_articles",
                column: "question",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_filter_action_filter_id",
                table: "filter_action",
                column: "filter_id");

            migrationBuilder.CreateIndex(
                name: "ix_filter_rule_filter_id_what_how_value",
                table: "filter_rule",
                columns: new[] { "filter_id", "what", "how", "value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_form_entries_form_definition_id",
                table: "form_entries",
                column: "form_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_entries_object_type_object_id",
                table: "form_entries",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "ix_form_entry_value_form_field_id",
                table: "form_entry_value",
                column: "form_field_id");

            migrationBuilder.CreateIndex(
                name: "ix_form_field_form_definition_id_name",
                table: "form_field",
                columns: new[] { "form_definition_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_help_topic_form_form_definition_id",
                table: "help_topic_form",
                column: "form_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_help_topic_form_help_topic_id_form_definition_id",
                table: "help_topic_form",
                columns: new[] { "help_topic_id", "form_definition_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_help_topics_department_id",
                table: "help_topics",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_help_topics_name_parent_id",
                table: "help_topics",
                columns: new[] { "name", "parent_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_help_topics_parent_id",
                table: "help_topics",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_kb_categories_parent_id",
                table: "kb_categories",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_list_item_list_definition_id",
                table: "list_item",
                column: "list_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_name",
                table: "organizations",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_queue_column_column_id",
                table: "saved_queue_column",
                column: "column_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_queue_export_field_queue_id",
                table: "saved_queue_export_field",
                column: "queue_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_queue_sort_sort_option_id",
                table: "saved_queue_sort",
                column: "sort_option_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_queues_parent_id",
                table: "saved_queues",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_queues_staff_id",
                table: "saved_queues",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedule_entry_schedule_id",
                table: "schedule_entry",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_sequences_name",
                table: "sequences",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_settings_namespace_key",
                table: "settings",
                columns: new[] { "namespace", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_site_pages_name",
                table: "site_pages",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_plans_name",
                table: "sla_plans",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_plans_schedule_id",
                table: "sla_plans",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_department_id",
                table: "staff",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_identity_user_id",
                table: "staff",
                column: "identity_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_is_active",
                table: "staff",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_staff_on_vacation",
                table: "staff",
                column: "on_vacation");

            migrationBuilder.CreateIndex(
                name: "ix_staff_role_id",
                table: "staff",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_username",
                table: "staff",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_department_access_department_id",
                table: "staff_department_access",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_department_access_role_id",
                table: "staff_department_access",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_signature",
                table: "stored_files",
                column: "signature");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_storage_key",
                table: "stored_files",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_system_log_entries_created_at",
                table: "system_log_entries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_system_log_entries_type",
                table: "system_log_entries",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_task_items_department_id",
                table: "task_items",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_items_number",
                table: "task_items",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_items_staff_id",
                table: "task_items",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_items_team_id",
                table: "task_items",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_items_thread_id",
                table: "task_items",
                column: "thread_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_items_ticket_id",
                table: "task_items",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_member_staff_id",
                table: "team_member",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_teams_name",
                table: "teams",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_collaborators_thread_id_user_id",
                table: "thread_collaborators",
                columns: new[] { "thread_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_collaborators_user_id",
                table: "thread_collaborators",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_thread_entries_thread_id",
                table: "thread_entries",
                column: "thread_id");

            migrationBuilder.CreateIndex(
                name: "ix_thread_entries_type",
                table: "thread_entries",
                column: "type");

            migrationBuilder.CreateIndex(
                name: "ix_thread_event_types_name",
                table: "thread_event_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_events_event_type_id",
                table: "thread_events",
                column: "event_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_thread_events_occurred_at_event_type_id",
                table: "thread_events",
                columns: new[] { "occurred_at", "event_type_id" });

            migrationBuilder.CreateIndex(
                name: "ix_thread_events_thread_id_event_type_id_occurred_at",
                table: "thread_events",
                columns: new[] { "thread_id", "event_type_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_priorities_key",
                table: "ticket_priorities",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_statuses_key",
                table: "ticket_statuses",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_statuses_name",
                table: "ticket_statuses",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_statuses_state",
                table: "ticket_statuses",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_closed_at",
                table: "tickets",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_created_at",
                table: "tickets",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_department_id",
                table: "tickets",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_due_date",
                table: "tickets",
                column: "due_date");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_help_topic_id",
                table: "tickets",
                column: "help_topic_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_number",
                table: "tickets",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_parent_id",
                table: "tickets",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_priority_id",
                table: "tickets",
                column: "priority_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_sla_id",
                table: "tickets",
                column: "sla_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_staff_id",
                table: "tickets",
                column: "staff_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_status_id",
                table: "tickets",
                column: "status_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_team_id",
                table: "tickets",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_thread_id",
                table: "tickets",
                column: "thread_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_user_id",
                table: "tickets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_emails_address",
                table: "user_emails",
                column: "address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_emails_user_id",
                table: "user_emails",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_default_email_id",
                table: "users",
                column: "default_email_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_identity_user_id",
                table: "users",
                column: "identity_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_name",
                table: "users",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_id",
                table: "users",
                column: "organization_id");

            migrationBuilder.AddForeignKey(
                name: "fk_effort_proposals_tickets_ticket_id",
                table: "effort_proposals",
                column: "ticket_id",
                principalTable: "tickets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_task_items_tickets_ticket_id",
                table: "task_items",
                column: "ticket_id",
                principalTable: "tickets",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_thread_collaborators_users_user_id",
                table: "thread_collaborators",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_users_user_id",
                table: "tickets",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_user_emails_users_user_id",
                table: "user_emails",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_emails_users_user_id",
                table: "user_emails");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "attachments");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "banlist_entries");

            migrationBuilder.DropTable(
                name: "canned_responses");

            migrationBuilder.DropTable(
                name: "drafts");

            migrationBuilder.DropTable(
                name: "edit_locks");

            migrationBuilder.DropTable(
                name: "effort_proposals");

            migrationBuilder.DropTable(
                name: "email_channel");

            migrationBuilder.DropTable(
                name: "email_template");

            migrationBuilder.DropTable(
                name: "faq_article_topic");

            migrationBuilder.DropTable(
                name: "filter_action");

            migrationBuilder.DropTable(
                name: "filter_rule");

            migrationBuilder.DropTable(
                name: "form_entry_value");

            migrationBuilder.DropTable(
                name: "help_topic_form");

            migrationBuilder.DropTable(
                name: "list_item");

            migrationBuilder.DropTable(
                name: "saved_queue_column");

            migrationBuilder.DropTable(
                name: "saved_queue_export_field");

            migrationBuilder.DropTable(
                name: "saved_queue_sort");

            migrationBuilder.DropTable(
                name: "schedule_entry");

            migrationBuilder.DropTable(
                name: "sequences");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "site_pages");

            migrationBuilder.DropTable(
                name: "staff_department_access");

            migrationBuilder.DropTable(
                name: "system_log_entries");

            migrationBuilder.DropTable(
                name: "task_items");

            migrationBuilder.DropTable(
                name: "team_member");

            migrationBuilder.DropTable(
                name: "thread_collaborators");

            migrationBuilder.DropTable(
                name: "thread_entries");

            migrationBuilder.DropTable(
                name: "thread_events");

            migrationBuilder.DropTable(
                name: "stored_files");

            migrationBuilder.DropTable(
                name: "email_accounts");

            migrationBuilder.DropTable(
                name: "email_template_sets");

            migrationBuilder.DropTable(
                name: "faq_articles");

            migrationBuilder.DropTable(
                name: "filters");

            migrationBuilder.DropTable(
                name: "form_entries");

            migrationBuilder.DropTable(
                name: "form_field");

            migrationBuilder.DropTable(
                name: "list_definitions");

            migrationBuilder.DropTable(
                name: "queue_columns");

            migrationBuilder.DropTable(
                name: "queue_sort_options");

            migrationBuilder.DropTable(
                name: "saved_queues");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "thread_event_types");

            migrationBuilder.DropTable(
                name: "kb_categories");

            migrationBuilder.DropTable(
                name: "form_definitions");

            migrationBuilder.DropTable(
                name: "help_topics");

            migrationBuilder.DropTable(
                name: "staff");

            migrationBuilder.DropTable(
                name: "teams");

            migrationBuilder.DropTable(
                name: "threads");

            migrationBuilder.DropTable(
                name: "ticket_priorities");

            migrationBuilder.DropTable(
                name: "ticket_statuses");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "sla_plans");

            migrationBuilder.DropTable(
                name: "schedules");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "user_emails");
        }
    }
}
