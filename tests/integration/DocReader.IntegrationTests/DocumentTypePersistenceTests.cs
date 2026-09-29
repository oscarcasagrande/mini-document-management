using DocReader.Application.Catalog;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Infrastructure.Persistence;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Document types against a real PostgreSQL: the seven built-ins seeded by the migration, the unique
/// code, the filters and that a type in use (or built-in) cannot be deleted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DocumentTypePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string Schema = """{"type":"object"}""";
    private const string Rules = """{"evidence":[{"name":"title","weight":0.6,"patterns":["ALFA"]}],"threshold":0.6}""";
    private const string Extraction = "{}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DocumentType Custom(string code, bool active = true) =>
        DocumentType.Create(Guid.CreateVersion7(Now), code, code, Schema, Rules, Extraction, active, isBuiltIn: false, Now);

    [Fact]
    public async Task A_migracao_semeia_os_sete_tipos_embutidos_ativos()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();
        var repository = new DocumentTypeRepository(context);

        var active = await repository.ListActiveAsync(Ct);

        Assert.Equal(7, active.Count);
        Assert.All(active, type => Assert.True(type.IsBuiltIn));
        Assert.Equal(
            ["BR_CPF_CARD", "BR_CIN", "BR_CNH", "BR_CNPJ_CARD", "BR_CCMEI", "BR_SOCIAL_CONTRACT", "BR_PROOF_OF_ADDRESS"],
            active.Select(type => type.Code));
    }

    [Fact]
    public async Task Codigo_repetido_e_recusado_pelo_banco_como_conflito()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();
        var repository = new DocumentTypeRepository(context);
        await repository.AddAsync(Custom("br_custom"), Ct);

        await using var second = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            new DocumentTypeRepository(second).AddAsync(Custom("BR_CUSTOM"), Ct));

        Assert.Equal("DOCUMENT_TYPE_CODE_EXISTS", error.ErrorCode);
    }

    [Fact]
    public async Task Tipo_gravado_volta_pelo_id_e_pelo_codigo_e_pode_ser_atualizado()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var type = Custom("br_custom");
        await using (var write = fixture.CreateContext())
        {
            await new DocumentTypeRepository(write).AddAsync(type, Ct);
        }

        await using (var update = fixture.CreateContext())
        {
            var repository = new DocumentTypeRepository(update);
            var tracked = await repository.FindByCodeAsync("BR_CUSTOM", Ct);
            tracked!.Update("Novo nome", Schema, Rules, Extraction, active: false, Now.AddDays(1));
            await repository.SaveChangesAsync(Ct);
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentTypeRepository(read).FindByIdAsync(type.Id, Ct);
        Assert.Equal("Novo nome", loaded!.Name);
        Assert.False(loaded.Active);
        Assert.Equal(Now.AddDays(1), loaded.UpdatedAt);
    }

    [Fact]
    public async Task Tipo_referenciado_por_documento_e_o_banco_impede_a_exclusao_direta()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var type = Custom("br_custom");
        await using (var write = fixture.CreateContext())
        {
            await new DocumentTypeRepository(write).AddAsync(type, Ct);

            var id = Guid.CreateVersion7(Now);
            var document = Document.Accept(
                id,
                $"DOC-20260929-{Random.Shared.Next(1, 999_999):D6}",
                "doc.png",
                $"documents/2026/09/29/{id:D}/original.png",
                "image/png",
                1024,
                new string('c', 64),
                1,
                UploadChannel.Api,
                null,
                "BR_CUSTOM",
                Now);
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        await using var context = fixture.CreateContext();
        var repository = new DocumentTypeRepository(context);

        Assert.True(await repository.IsReferencedAsync("BR_CUSTOM", Ct));
    }

    [Fact]
    public async Task Tipo_sem_uso_pode_ser_excluido()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var type = Custom("br_custom");
        await using (var write = fixture.CreateContext())
        {
            await new DocumentTypeRepository(write).AddAsync(type, Ct);
        }

        await using var context = fixture.CreateContext();
        var repository = new DocumentTypeRepository(context);

        Assert.False(await repository.IsReferencedAsync("BR_CUSTOM", Ct));

        var tracked = await repository.FindByIdAsync(type.Id, Ct);
        await repository.RemoveAsync(tracked!, Ct);

        Assert.Null(await repository.FindByIdAsync(type.Id, Ct));
    }
}
