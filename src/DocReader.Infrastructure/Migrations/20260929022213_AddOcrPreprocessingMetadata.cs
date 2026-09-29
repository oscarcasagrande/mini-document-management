using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrPreprocessingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "deskewed",
                table: "extractions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "has_native_text_layer",
                table: "extractions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ocr_processed_with_structure",
                table: "extractions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "rotation_degrees",
                table: "extractions",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deskewed",
                table: "extractions");

            migrationBuilder.DropColumn(
                name: "has_native_text_layer",
                table: "extractions");

            migrationBuilder.DropColumn(
                name: "ocr_processed_with_structure",
                table: "extractions");

            migrationBuilder.DropColumn(
                name: "rotation_degrees",
                table: "extractions");
        }
    }
}
