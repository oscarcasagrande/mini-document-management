using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageMigrationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "storage_migration_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    filter_document_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    filter_product_service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    filter_uploaded_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    filter_uploaded_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    documents_migrated = table.Column<int>(type: "integer", nullable: false),
                    documents_failed = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_migration_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_storage_migration_jobs_storage_repositories_source_reposito~",
                        column: x => x.source_repository_id,
                        principalTable: "storage_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_storage_migration_jobs_storage_repositories_target_reposito~",
                        column: x => x.target_repository_id,
                        principalTable: "storage_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_storage_migration_jobs_source_repository_id",
                table: "storage_migration_jobs",
                column: "source_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_storage_migration_jobs_status_requested_at",
                table: "storage_migration_jobs",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "IX_storage_migration_jobs_target_repository_id",
                table: "storage_migration_jobs",
                column: "target_repository_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_migration_jobs");
        }
    }
}
