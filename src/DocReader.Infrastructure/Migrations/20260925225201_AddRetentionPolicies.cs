using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetentionPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purged_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "retention_days",
                table: "documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "retention_policy_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "retention_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    product_service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policies", x => x.id);
                    table.CheckConstraint("ck_retention_policies_days", "retention_days >= 1 AND retention_days <= 36500");
                    table.ForeignKey(
                        name: "FK_retention_policies_product_services_product_service_id",
                        column: x => x.product_service_id,
                        principalTable: "product_services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_expires_at_pending",
                table: "documents",
                column: "expires_at",
                filter: "status <> 'PURGED' AND expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_documents_retention_policy_id",
                table: "documents",
                column: "retention_policy_id");

            migrationBuilder.CreateIndex(
                name: "IX_retention_policies_product_service_id",
                table: "retention_policies",
                column: "product_service_id");

            migrationBuilder.CreateIndex(
                name: "ux_retention_policies_scope",
                table: "retention_policies",
                columns: new[] { "document_type", "product_service_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_retention_policies_retention_policy_id",
                table: "documents",
                column: "retention_policy_id",
                principalTable: "retention_policies",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Exactly one global policy must exist; the API refuses to create or delete it. The default is one year.
            migrationBuilder.Sql(
                """
                INSERT INTO retention_policies (id, document_type, product_service_id, retention_days, created_at, updated_at)
                VALUES ('00000000-0000-7000-8000-000000000001', NULL, NULL, 365, now(), now())
                ON CONFLICT DO NOTHING;
                """);

            // Documents that predate retention get the global policy, counted from their upload, so every document has a deadline.
            migrationBuilder.Sql(
                """
                UPDATE documents
                SET retention_policy_id = '00000000-0000-7000-8000-000000000001',
                    retention_days = 365,
                    expires_at = uploaded_at + interval '365 days'
                WHERE expires_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_documents_retention_policies_retention_policy_id",
                table: "documents");

            migrationBuilder.DropTable(
                name: "retention_policies");

            migrationBuilder.DropIndex(
                name: "ix_documents_expires_at_pending",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_documents_retention_policy_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "purged_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "retention_days",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "retention_policy_id",
                table: "documents");
        }
    }
}
