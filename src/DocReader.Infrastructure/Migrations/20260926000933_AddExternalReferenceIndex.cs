using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalReferenceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_documents_external_reference_uploaded_at",
                table: "documents",
                columns: new[] { "external_reference", "uploaded_at" },
                filter: "external_reference IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_external_reference_uploaded_at",
                table: "documents");
        }
    }
}
