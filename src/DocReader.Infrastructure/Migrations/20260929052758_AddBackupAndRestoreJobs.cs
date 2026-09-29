using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupAndRestoreJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    storage_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    file_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    document_count = table.Column<int>(type: "integer", nullable: true),
                    files_archived = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "restore_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archive_storage_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archive_storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    archive_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    archive_checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    document_count = table.Column<int>(type: "integer", nullable: false),
                    files_restored = table.Column<int>(type: "integer", nullable: true),
                    error_message = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_restore_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_state",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_jobs_status_requested_at",
                table: "backup_jobs",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_restore_jobs_status_requested_at",
                table: "restore_jobs",
                columns: new[] { "status", "requested_at" });

            // At most one restore pending or running at any time, enforced by the database rather than by a check
            // that two concurrent uploads could both pass.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_restore_jobs_single_active ON restore_jobs ((true))
                WHERE status IN ('PENDING', 'RUNNING');
                """);

            // The one row of the read-only gate, lowered.
            migrationBuilder.Sql(
                """
                INSERT INTO system_state (id, is_read_only, updated_at)
                VALUES ('00000000-0000-7000-8000-0000000005a1', false, now())
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_jobs");

            migrationBuilder.DropTable(
                name: "restore_jobs");

            migrationBuilder.DropTable(
                name: "system_state");
        }
    }
}
