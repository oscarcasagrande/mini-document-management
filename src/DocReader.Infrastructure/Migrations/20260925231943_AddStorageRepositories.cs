using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStorageRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "storage_repository_id",
                table: "product_services",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "storage_repository_id",
                table: "documents",
                type: "uuid",
                nullable: false,
                // Every document that exists was stored by the file system adapter, in what becomes the default repository.
                defaultValue: new Guid("00000000-0000-7000-8000-0000000000d1"));

            migrationBuilder.CreateTable(
                name: "document_blobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_blobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "storage_repositories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_config = table.Column<string>(type: "jsonb", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_repositories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_services_storage_repository_id",
                table: "product_services",
                column: "storage_repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_storage_repository_id",
                table: "documents",
                column: "storage_repository_id");

            migrationBuilder.CreateIndex(
                name: "ux_document_blobs_document_id",
                table: "document_blobs",
                column: "document_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_storage_repositories_code",
                table: "storage_repositories",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_storage_repositories_default",
                table: "storage_repositories",
                column: "is_default",
                unique: true,
                filter: "is_default");

            // The default repository: the file system volume the system has always used, with no settings (it takes the
            // configured storage root). It must exist before the foreign key that documents point at it.
            migrationBuilder.Sql(
                """
                INSERT INTO storage_repositories (id, code, name, provider, connection_config, is_default, active, created_at, updated_at)
                VALUES ('00000000-0000-7000-8000-0000000000d1', 'DEFAULT', 'Default (file system volume)', 'FILE_SYSTEM', NULL, true, true, now(), now())
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_storage_repositories_storage_repository_id",
                table: "documents",
                column: "storage_repository_id",
                principalTable: "storage_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_product_services_storage_repositories_storage_repository_id",
                table: "product_services",
                column: "storage_repository_id",
                principalTable: "storage_repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_documents_storage_repositories_storage_repository_id",
                table: "documents");

            migrationBuilder.DropForeignKey(
                name: "FK_product_services_storage_repositories_storage_repository_id",
                table: "product_services");

            migrationBuilder.DropTable(
                name: "document_blobs");

            migrationBuilder.DropTable(
                name: "storage_repositories");

            migrationBuilder.DropIndex(
                name: "IX_product_services_storage_repository_id",
                table: "product_services");

            migrationBuilder.DropIndex(
                name: "ix_documents_storage_repository_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "storage_repository_id",
                table: "product_services");

            migrationBuilder.DropColumn(
                name: "storage_repository_id",
                table: "documents");
        }
    }
}
