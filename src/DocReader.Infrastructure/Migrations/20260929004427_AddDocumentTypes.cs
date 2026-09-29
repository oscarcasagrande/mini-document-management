using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DocReader.Application.Classification;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocReader.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentTypes : Migration
    {
        // Order of DocumentTypeProfile.All: the tie-break order of the rules classifier before this
        // migration, preserved here through created_at so moving the rules to the database changes
        // nothing about which type wins when two score the same.
        private static readonly string[] BuiltInCodes =
        [
            "BR_CPF_CARD", "BR_CIN", "BR_CNH", "BR_CNPJ_CARD", "BR_CCMEI", "BR_SOCIAL_CONTRACT", "BR_PROOF_OF_ADDRESS"
        ];

        private static readonly IReadOnlyDictionary<string, Guid> BuiltInIds = new Dictionary<string, Guid>
        {
            ["BR_CPF_CARD"] = Guid.Parse("00000000-0000-7000-8000-000000000101"),
            ["BR_CIN"] = Guid.Parse("00000000-0000-7000-8000-000000000102"),
            ["BR_CNH"] = Guid.Parse("00000000-0000-7000-8000-000000000103"),
            ["BR_CNPJ_CARD"] = Guid.Parse("00000000-0000-7000-8000-000000000104"),
            ["BR_CCMEI"] = Guid.Parse("00000000-0000-7000-8000-000000000105"),
            ["BR_SOCIAL_CONTRACT"] = Guid.Parse("00000000-0000-7000-8000-000000000106"),
            ["BR_PROOF_OF_ADDRESS"] = Guid.Parse("00000000-0000-7000-8000-000000000107"),
        };

        private static readonly DateTimeOffset SeedBasis = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    classification_rules = table.Column<string>(type: "jsonb", nullable: false),
                    extraction_rules = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_types", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_types_created_at",
                table: "document_types",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ux_document_types_code",
                table: "document_types",
                column: "code",
                unique: true);

            SeedBuiltInTypes(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_types");
        }

        /// <summary>
        /// The seven types migrate from code: the classification rules come from
        /// <see cref="DocumentTypeProfile.All"/> (the hardcoded profiles this migration replaces as the
        /// source classification reads from), and the schema and the display name come from the matching
        /// <c>schemas/documents/*.v1.json</c> file, embedded in this assembly at
        /// <c>Migrations/Seeds</c> so the migration never depends on files outside it.
        /// </summary>
        private static void SeedBuiltInTypes(MigrationBuilder migrationBuilder)
        {
            var profilesByCode = DocumentTypeProfile.All.ToDictionary(profile => profile.DocumentType);
            var assembly = typeof(AddDocumentTypes).Assembly;

            var values = new object[BuiltInCodes.Length, 10];

            for (var i = 0; i < BuiltInCodes.Length; i++)
            {
                var code = BuiltInCodes[i];
                var profile = profilesByCode[code];
                var schemaJson = ReadEmbeddedSchema(assembly, code);
                var (name, extractionRulesJson) = DescribeSchema(schemaJson);
                var now = SeedBasis.AddSeconds(i);

                values[i, 0] = BuiltInIds[code];
                values[i, 1] = code;
                values[i, 2] = name;
                values[i, 3] = schemaJson;
                values[i, 4] = profile.ToClassificationRulesJson();
                values[i, 5] = extractionRulesJson;
                values[i, 6] = true;
                values[i, 7] = true;
                values[i, 8] = now;
                values[i, 9] = now;
            }

            migrationBuilder.InsertData(
                table: "document_types",
                columns:
                [
                    "id", "code", "name", "schema", "classification_rules", "extraction_rules", "active",
                    "is_built_in", "created_at", "updated_at"
                ],
                values: values);
        }

        private static string ReadEmbeddedSchema(Assembly assembly, string code)
        {
            var suffix = $".Seeds.{code}.v1.json";
            var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new System.IO.StreamReader(stream);

            return reader.ReadToEnd();
        }

        private static (string Name, string ExtractionRulesJson) DescribeSchema(string schemaJson)
        {
            using var document = JsonDocument.Parse(schemaJson);
            var name = document.RootElement.GetProperty("title").GetString()!;
            var fields = document.RootElement.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(property => property.Name).ToArray()
                : [];

            return (name, JsonSerializer.Serialize(new { fields }));
        }
    }
}
