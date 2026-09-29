using DocReader.Application.Documents;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Encryption at rest of <c>extracted_fields.raw_value</c>/<c>normalized_value</c>, against a real PostgreSQL: a
/// value saved through the real <see cref="DocReaderDbContext"/> and read back through another instance gives the
/// original plain text (the EF value converter is transparent), and the bytes actually sitting in the column are
/// never the plain text (a raw SQL query bypasses EF entirely, the same trick
/// <c>StoragePersistenceTests.A_configuracao_cifrada_atravessa_o_jsonb_...</c> uses for the storage repository
/// connection settings).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FieldEncryptionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string PlainCpf = "111.444.777-35";
    private const string PlainName = "Fulano de Tal da Silva";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PostgresProcessingQueue QueueAt(DocReaderDbContext context, DateTimeOffset at) =>
        new(
            context,
            Options.Create(new ProcessingQueueOptions { WorkerName = "worker-test" }),
            new FakeTimeProvider(at),
            NullLogger<PostgresProcessingQueue>.Instance);

    private DocumentProcessingStore StoreFor(DocReaderDbContext context) => new(context, new FakeTimeProvider(Now.AddSeconds(30)));

    private async Task<Guid> SeedDocumentAsync()
    {
        await using var context = fixture.CreateContext();

        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            $"DOC-20260929-{Random.Shared.Next(1, 999_999):D6}",
            "cartao-cpf.png",
            $"documents/2026/09/29/{id:D}/original.png",
            "image/png",
            2048,
            new string('b', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            Now);

        document.MarkQueued(Now);
        context.Documents.Add(document);
        await context.SaveChangesAsync(Ct);

        return id;
    }

    private static DocumentExtraction NewExtraction(Guid documentId, Guid jobId) =>
        DocumentExtraction.Create(
            documentId,
            jobId,
            "paddleocr",
            "PP-OCRv5 test",
            "rules-1.0.0",
            "br-cpf-card-1.0.0",
            1,
            "REPUBLICA FEDERATIVA DO BRASIL\n111.444.777-35",
            "[{\"pageNumber\":1,\"text\":\"REPUBLICA FEDERATIVA DO BRASIL\\n111.444.777-35\"}]",
            "{\"pages\":[{\"page\":1,\"raw\":{}}]}",
            "{\"cpf\":\"11144477735\"}",
            0.99m,
            Now,
            [
                ExtractedField.Create("cpf", PlainCpf, "11144477735", 1.0m, 1, "[110,332,470,332,470,380,110,380]", "VALID", "[\"CHECK_DIGIT_VALID\"]"),
                ExtractedField.Create("name", PlainName, PlainName, 0.95m, 1, "[10,10,200,10,200,40,10,40]", "UNCERTAIN", "[]"),
                ExtractedField.Create("missing", null, null, null, null, "[]", "NOT_FOUND", "[]")
            ]);

    private async Task<Guid> CompleteExtractionAsync()
    {
        var documentId = await SeedDocumentAsync();

        await using (var context = fixture.CreateContext())
        {
            var queue = QueueAt(context, Now);
            await queue.EnqueueAsync(documentId, Ct);
            var job = await queue.AcquireNextAsync(Ct);

            var completed = await StoreFor(context).CompleteAsync(
                job!, NewExtraction(documentId, job!.Id), "BR_CPF_CARD", 1.0m, null, null, Ct);

            Assert.True(completed);
        }

        return documentId;
    }

    [Fact]
    public async Task Um_campo_gravado_e_lido_de_volta_pelo_EF_da_o_texto_puro_original()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await CompleteExtractionAsync();

        await using var verification = fixture.CreateContext();
        var extraction = await verification.Extractions.AsNoTracking().Include(e => e.Fields)
            .SingleAsync(e => e.DocumentId == documentId, Ct);

        var cpf = extraction.Fields.Single(f => f.FieldPath == "cpf");
        Assert.Equal(PlainCpf, cpf.RawValue);
        Assert.Equal("11144477735", cpf.NormalizedValue);

        var name = extraction.Fields.Single(f => f.FieldPath == "name");
        Assert.Equal(PlainName, name.RawValue);
        Assert.Equal(PlainName, name.NormalizedValue);

        var missing = extraction.Fields.Single(f => f.FieldPath == "missing");
        Assert.Null(missing.RawValue);
        Assert.Null(missing.NormalizedValue);
    }

    [Fact]
    public async Task O_valor_gravado_no_banco_nao_e_o_texto_puro_e_tem_a_cara_do_envelope_AES_GCM()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await CompleteExtractionAsync();

        // Bypasses EF (and its decrypting value converter) entirely: a raw SQL query against the column, the same
        // trick used for the storage repository's encrypted connection settings.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ef.raw_value, ef.normalized_value
            FROM extracted_fields ef
            JOIN extractions x ON x.id = ef.extraction_id
            WHERE x.document_id = @documentId AND ef.field_path = 'cpf'
            """;
        command.Parameters.AddWithValue("documentId", documentId);

        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        var storedRawValue = reader.GetString(0);
        var storedNormalizedValue = reader.GetString(1);

        Assert.DoesNotContain(PlainCpf, storedRawValue, StringComparison.Ordinal);
        Assert.DoesNotContain("11144477735", storedNormalizedValue, StringComparison.Ordinal);

        // The envelope is JSON with the same shape AesGcmEnvelope produces (version, algorithm, nonce, ciphertext, tag).
        Assert.Contains("\"alg\":\"AES-256-GCM\"", storedRawValue, StringComparison.Ordinal);
        Assert.Contains("\"alg\":\"AES-256-GCM\"", storedNormalizedValue, StringComparison.Ordinal);

        // And the same protector, with the key this fixture uses for the whole run, decrypts it back.
        Assert.Equal(PlainCpf, PostgresFixture.FieldEncryptionProtector.Unprotect(storedRawValue));
        Assert.Equal("11144477735", PostgresFixture.FieldEncryptionProtector.Unprotect(storedNormalizedValue));
    }

    [Fact]
    public async Task Um_campo_nao_encontrado_fica_nulo_no_banco_em_vez_de_um_envelope_vazio()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await CompleteExtractionAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ef.raw_value, ef.normalized_value
            FROM extracted_fields ef
            JOIN extractions x ON x.id = ef.extraction_id
            WHERE x.document_id = @documentId AND ef.field_path = 'missing'
            """;
        command.Parameters.AddWithValue("documentId", documentId);

        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));

        Assert.True(await reader.IsDBNullAsync(0, Ct));
        Assert.True(await reader.IsDBNullAsync(1, Ct));
    }
}
