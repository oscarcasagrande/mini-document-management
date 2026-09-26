using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_service_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_services",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_services", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_product_service_id",
                table: "documents",
                column: "product_service_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_services_code",
                table: "product_services",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_product_services_product_service_id",
                table: "documents",
                column: "product_service_id",
                principalTable: "product_services",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_documents_product_services_product_service_id",
                table: "documents");

            migrationBuilder.DropTable(
                name: "product_services");

            migrationBuilder.DropIndex(
                name: "ix_documents_product_service_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "product_service_id",
                table: "documents");
        }
    }
}
