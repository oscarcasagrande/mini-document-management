using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOcrResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_processing_jobs_document_id",
                table: "processing_jobs");

            migrationBuilder.AddColumn<int>(
                name: "page_count",
                table: "processing_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "pages_completed",
                table: "processing_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "extractions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ocr_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ocr_model_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    classifier_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    extractor_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schema_version = table.Column<int>(type: "integer", nullable: true),
                    raw_text = table.Column<string>(type: "text", nullable: false),
                    page_texts = table.Column<string>(type: "jsonb", nullable: false),
                    raw_ocr_result = table.Column<string>(type: "jsonb", nullable: false),
                    structured_result = table.Column<string>(type: "jsonb", nullable: true),
                    overall_confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extractions", x => x.id);
                    table.ForeignKey(
                        name: "FK_extractions_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_extractions_processing_jobs_processing_job_id",
                        column: x => x.processing_job_id,
                        principalTable: "processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extracted_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    extraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_path = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    raw_value = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    normalized_value = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    page_number = table.Column<int>(type: "integer", nullable: true),
                    bounding_box = table.Column<string>(type: "jsonb", nullable: false),
                    validation_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    validation_messages = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extracted_fields", x => x.id);
                    table.ForeignKey(
                        name: "FK_extracted_fields_extractions_extraction_id",
                        column: x => x.extraction_id,
                        principalTable: "extractions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_processing_jobs_document_id_active",
                table: "processing_jobs",
                column: "document_id",
                unique: true,
                filter: "status IN ('PENDING', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "ix_extracted_fields_extraction_id_field_path",
                table: "extracted_fields",
                columns: new[] { "extraction_id", "field_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extractions_document_id_created_at",
                table: "extractions",
                columns: new[] { "document_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_extractions_processing_job_id",
                table: "extractions",
                column: "processing_job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extracted_fields");

            migrationBuilder.DropTable(
                name: "extractions");

            migrationBuilder.DropIndex(
                name: "ux_processing_jobs_document_id_active",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "page_count",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "pages_completed",
                table: "processing_jobs");

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_document_id",
                table: "processing_jobs",
                column: "document_id");
        }
    }
}
