using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurgeExtractionsOfPurgedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only. Purging used to remove just the original file; documents purged that way still hold the text
            // the OCR read. Their extractions (and, by cascade, their fields) go now, like in every purge from here on.
            migrationBuilder.Sql("DELETE FROM extractions WHERE document_id IN (SELECT id FROM documents WHERE status = 'PURGED');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted content cannot be restored.
        }
    }
}
