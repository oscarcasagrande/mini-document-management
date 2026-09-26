using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Storage;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Storage repositories against a real PostgreSQL: the seeded default, the rule that exactly one is the default (also under
/// concurrency), the database adapter with real bytea content, and the whole facade with the real encryption and the settings
/// going through a jsonb column.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StoragePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static StorageRepository Repository(string code, StorageProvider provider = StorageProvider.Database, bool active = true) =>
        StorageRepository.Create(Guid.CreateVersion7(Now), code, code, provider, null, false, active, Now);

    private StorageRepositoryStore StoreOf(DocReaderDbContext context) => new(context, new FakeTimeProvider(Now));

    private static Document DocumentIn(Guid repositoryId, Guid? productServiceId = null)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, $"DOC-20260925-{Random.Shared.Next(1, 999_999):D6}", "doc.png", $"blobs/{id:D}", "image/png", 10, new string('e', 64), 1,
            UploadChannel.Api, null, null, Now, productServiceId, null, repositoryId);
        document.MarkQueued(Now);

        return document;
    }

    // ---- migration ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_migration_cria_o_repositorio_padrao_de_filesystem()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();
        var defaults = await context.StorageRepositories.AsNoTracking().Where(repository => repository.IsDefault).ToListAsync(Ct);

        var only = Assert.Single(defaults);
        Assert.Equal(StorageRepository.DefaultRepositoryId, only.Id);
        Assert.Equal(StorageProvider.FileSystem, only.Provider);
        Assert.True(only.Active);
        Assert.Null(only.EncryptedConnectionConfig);
    }

    [Fact]
    public async Task O_banco_recusa_dois_padroes_e_codigo_repetido()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();
        var second = StorageRepository.Create(Guid.CreateVersion7(Now), "OUTRO", "outro", StorageProvider.Database, null, true, true, Now);
        context.StorageRepositories.Add(second);

        var noTwoDefaults = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)noTwoDefaults.InnerException!).SqlState);

        await using var duplicate = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            StoreOf(duplicate).AddAsync(Repository("default"), makeDefault: false, Ct));
        Assert.Equal("STORAGE_REPOSITORY_CODE_EXISTS", error.ErrorCode);
    }

    [Fact]
    public async Task Documentos_novos_sem_repositorio_explicito_apontam_para_o_padrao()
    {
        RequireDatabase(fixture);

        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(id, "DOC-20260925-000001", "a.png", $"documents/{id:D}", "image/png", 10, new string('a', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkQueued(Now);

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = fixture.CreateContext();
        Assert.Equal(StorageRepository.DefaultRepositoryId, (await new DocumentRepository(read).FindByIdAsync(id, false, Ct))!.StorageRepositoryId);
    }

    // ---- o padrão único --------------------------------------------------------------------------------------

    [Fact]
    public async Task Adicionar_como_padrao_troca_atomicamente()
    {
        RequireDatabase(fixture);

        var created = Repository("novo");
        await using (var write = fixture.CreateContext())
        {
            await StoreOf(write).AddAsync(created, makeDefault: true, Ct);
        }

        await using var read = fixture.CreateContext();
        var defaults = await read.StorageRepositories.AsNoTracking().Where(repository => repository.IsDefault).ToListAsync(Ct);

        Assert.Equal(created.Id, Assert.Single(defaults).Id);
    }

    [Fact]
    public async Task Definir_o_padrao_deixa_exatamente_um()
    {
        RequireDatabase(fixture);

        var one = Repository("um");
        var two = Repository("dois");
        await using (var write = fixture.CreateContext())
        {
            await StoreOf(write).AddAsync(one, makeDefault: false, Ct);
            await StoreOf(write).AddAsync(two, makeDefault: false, Ct);
        }

        await using (var switching = fixture.CreateContext())
        {
            await StoreOf(switching).SetDefaultAsync(two.Id, Now, Ct);
        }

        await using var read = fixture.CreateContext();
        var defaults = await read.StorageRepositories.AsNoTracking().Where(repository => repository.IsDefault).ToListAsync(Ct);

        Assert.Equal(two.Id, Assert.Single(defaults).Id);
    }

    [Fact]
    public async Task Duas_trocas_de_padrao_ao_mesmo_tempo_nunca_deixam_zero_nem_dois()
    {
        RequireDatabase(fixture);

        var one = Repository("um");
        var two = Repository("dois");
        await using (var write = fixture.CreateContext())
        {
            await StoreOf(write).AddAsync(one, makeDefault: false, Ct);
            await StoreOf(write).AddAsync(two, makeDefault: false, Ct);
        }

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var results = await Task.WhenAll(
            Attempt(() => StoreOf(contextA).SetDefaultAsync(one.Id, Now, Ct)),
            Attempt(() => StoreOf(contextB).SetDefaultAsync(two.Id, Now, Ct)));

        Assert.Contains(true, results);

        await using var read = fixture.CreateContext();
        Assert.Equal(1, await read.StorageRepositories.CountAsync(repository => repository.IsDefault, Ct));

        static async Task<bool> Attempt(Func<Task> action)
        {
            try
            {
                await action();

                return true;
            }
            catch (DbUpdateException)
            {
                return false;
            }
        }
    }

    // ---- referências -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Repositorio_com_documento_ou_produto_conta_como_referenciado_e_o_banco_impede_a_exclusao()
    {
        RequireDatabase(fixture);

        var withDocument = Repository("com-doc");
        var withProduct = Repository("com-produto");
        var free = Repository("livre");
        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        product.UseStorageRepository(withProduct.Id, Now);

        await using (var write = fixture.CreateContext())
        {
            var store = StoreOf(write);
            await store.AddAsync(withDocument, false, Ct);
            await store.AddAsync(withProduct, false, Ct);
            await store.AddAsync(free, false, Ct);
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            write.Documents.Add(DocumentIn(withDocument.Id));
            await write.SaveChangesAsync(Ct);
        }

        await using var context = fixture.CreateContext();
        var repository = StoreOf(context);

        Assert.True(await repository.IsReferencedAsync(withDocument.Id, Ct));
        Assert.True(await repository.HasDocumentsAsync(withDocument.Id, Ct));
        Assert.True(await repository.IsReferencedAsync(withProduct.Id, Ct));
        Assert.False(await repository.HasDocumentsAsync(withProduct.Id, Ct));
        Assert.False(await repository.IsReferencedAsync(free.Id, Ct));

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RemoveAsync((await repository.FindByIdAsync(withDocument.Id, Ct))!, Ct));
    }

    [Fact]
    public async Task Lista_traz_o_padrao_primeiro_e_filtra_por_provedor_e_ativo()
    {
        RequireDatabase(fixture);

        await using (var write = fixture.CreateContext())
        {
            var store = StoreOf(write);
            await store.AddAsync(Repository("db-um"), false, Ct);
            await store.AddAsync(Repository("db-off", active: false), false, Ct);
        }

        await using var read = fixture.CreateContext();
        var store2 = StoreOf(read);

        var all = await store2.ListAsync(new StorageRepositoryFilter(null, null, null, 1, 10), Ct);
        Assert.Equal("DEFAULT", all.Items[0].Code);
        Assert.Equal(3, all.TotalCount);

        Assert.Equal(2, (await store2.ListAsync(new StorageRepositoryFilter(null, StorageProvider.Database, null, 1, 10), Ct)).TotalCount);
        Assert.Equal(["DB-OFF"], (await store2.ListAsync(new StorageRepositoryFilter(null, null, false, 1, 10), Ct)).Items.Select(item => item.Code));
    }

    // ---- adapter de banco ---------------------------------------------------------------------------------------

    [Fact]
    public async Task O_adapter_de_banco_guarda_le_e_apaga_bytes_de_verdade()
    {
        RequireDatabase(fixture);

        var bytes = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var documentId = Guid.CreateVersion7(Now);
        var metadata = new FileMetadata(documentId, ".pdf", "application/pdf", Now);

        await using (var write = fixture.CreateContext())
        {
            await using var content = new MemoryStream(bytes);
            var stored = await new DatabaseStorageAdapter(write).SaveAsync(content, metadata, Ct);

            Assert.Equal($"blobs/{documentId:D}", stored.StorageKey);
            Assert.Equal(bytes.Length, stored.SizeBytes);
        }

        await using (var read = fixture.CreateContext())
        {
            await using var opened = await new DatabaseStorageAdapter(read).OpenReadAsync($"blobs/{documentId:D}", Ct);
            using var copy = new MemoryStream();
            await opened.CopyToAsync(copy, Ct);

            Assert.Equal(bytes, copy.ToArray());
        }

        await using (var delete = fixture.CreateContext())
        {
            var adapter = new DatabaseStorageAdapter(delete);
            await adapter.DeleteAsync($"blobs/{documentId:D}", Ct);
            await adapter.DeleteAsync($"blobs/{documentId:D}", Ct);
            await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.OpenReadAsync($"blobs/{documentId:D}", Ct));
        }
    }

    [Fact]
    public async Task Guardar_de_novo_o_mesmo_documento_substitui_o_conteudo_sem_erro()
    {
        RequireDatabase(fixture);

        var documentId = Guid.CreateVersion7(Now);
        var metadata = new FileMetadata(documentId, ".pdf", "application/pdf", Now);

        await using var context = fixture.CreateContext();
        var adapter = new DatabaseStorageAdapter(context);
        await using (var first = new MemoryStream([1, 1, 1]))
        {
            await adapter.SaveAsync(first, metadata, Ct);
        }

        await using (var second = new MemoryStream([2, 2]))
        {
            await adapter.SaveAsync(second, metadata, Ct);
        }

        await using var opened = await adapter.OpenReadAsync($"blobs/{documentId:D}", Ct);
        Assert.Equal(2, opened.Length);
        Assert.Equal(1, await context.DocumentBlobs.CountAsync(blob => blob.DocumentId == documentId, Ct));
    }

    [Theory]
    [InlineData("documents/2026/09/25/x/original.pdf")]
    [InlineData("blobs/nao-e-guid")]
    [InlineData("../blobs/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21")]
    public async Task O_adapter_de_banco_recusa_chave_com_formato_inesperado(string key)
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();

        await Assert.ThrowsAsync<ArgumentException>(() => new DatabaseStorageAdapter(context).OpenReadAsync(key, Ct));
    }

    [Fact]
    public async Task O_adapter_de_banco_diz_se_pode_gravar()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();

        Assert.True(await new DatabaseStorageAdapter(context).IsWritableAsync(Ct));
    }

    // ---- a fachada completa ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_configuracao_cifrada_atravessa_o_jsonb_e_a_fachada_usa_o_repositorio_de_banco_de_ponta_a_ponta()
    {
        RequireDatabase(fixture);

        var protector = new AesGcmSecretProtector(Options.Create(new SecretsOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        var repository = Repository("arquivo-db");

        await using (var write = fixture.CreateContext())
        {
            var service = new StorageRepositoryService(StoreOf(write), protector, new FakeTimeProvider(Now), NullLogger<StorageRepositoryService>.Instance);
            var created = await service.CreateAsync("cofre-fs", "Cofre", StorageProvider.FileSystem, new JsonObject { ["directory"] = "cofre" }, false, true, Ct);
            repository = created;
        }

        // The settings sit in a jsonb column: read them back from the database and decrypt what came out.
        await using (var read = fixture.CreateContext())
        {
            var stored = await read.StorageRepositories.AsNoTracking().SingleAsync(item => item.Id == repository.Id, Ct);

            Assert.DoesNotContain("cofre", stored.EncryptedConnectionConfig, StringComparison.Ordinal);
            var plain = (JsonObject)JsonNode.Parse(protector.Unprotect(stored.EncryptedConnectionConfig!))!;
            Assert.Equal("cofre", (string?)plain["directory"]);
        }

        // A database repository, used through the facade with the real adapter factory.
        var database = Repository("cofre-db");
        await using (var write = fixture.CreateContext())
        {
            await StoreOf(write).AddAsync(database, false, Ct);
        }

        await using var context = fixture.CreateContext();
        var factory = new StorageAdapterFactory(context, Options.Create(new StorageOptions { RootPath = Path.Combine(Path.GetTempPath(), "docreader-it") }), NullLoggerFactory.Instance);
        var facade = new RepositoryFileStorage(StoreOf(context), factory, protector);
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        await using var content = new MemoryStream([7, 7, 7, 7]);
        var saved = await facade.SaveAsync(database.Id, content, metadata, Ct);
        await using (var opened = await facade.OpenReadAsync(database.Id, saved.StorageKey, Ct))
        {
            Assert.Equal(4, opened.Length);
        }

        await facade.DeleteAsync(database.Id, saved.StorageKey, Ct);
        await Assert.ThrowsAsync<FileNotFoundException>(() => facade.OpenReadAsync(database.Id, saved.StorageKey, Ct));
        Assert.True(await facade.IsWritableAsync(Ct));
    }
}
