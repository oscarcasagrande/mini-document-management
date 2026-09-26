using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Files;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Storage repositories: the settings per provider, the rule that exactly one is the default, the choice of repository at
/// upload, and the facade that sends each call to the adapter of the repository the document lives in.
/// </summary>
public sealed class StorageRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryStorageRepositoryStore _store = new();
    private readonly FakeSecretProtector _protector = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StorageRepositoryService Service() =>
        new(_store, _protector, _clock, NullLogger<StorageRepositoryService>.Instance);

    private static JsonObject Config(params (string Key, string? Value)[] entries)
    {
        var config = new JsonObject();
        foreach (var (key, value) in entries)
        {
            config[key] = value is null ? null : JsonValue.Create(value);
        }

        return config;
    }

    private Task<StorageRepository> CreateAsync(
        string code,
        StorageProvider provider = StorageProvider.Database,
        JsonObject? config = null,
        bool isDefault = false,
        bool active = true) =>
        Service().CreateAsync(code, code, provider, config, isDefault, active, Ct);

    private JsonObject StoredConfig(StorageRepository repository) =>
        (JsonObject)JsonNode.Parse(_protector.Unprotect(repository.EncryptedConnectionConfig!))!;

    // ---- configuração por provedor ------------------------------------------------------------------------------

    [Fact]
    public void Filesystem_aceita_diretorio_relativo_e_recusa_o_que_escapa_da_raiz()
    {
        StorageConnectionConfig.Validate(StorageProvider.FileSystem, Config(("directory", "clientes/acme-01")));
        StorageConnectionConfig.Validate(StorageProvider.FileSystem, null);
        StorageConnectionConfig.Validate(StorageProvider.FileSystem, []);

        foreach (var bad in new[] { "/etc", "../fora", "a/../b", "a//b", "C:\\dados", "..", ".", "a b", "a/./b", "a/", "/" })
        {
            var error = Assert.Throws<RequestValidationException>(() =>
                StorageConnectionConfig.Validate(StorageProvider.FileSystem, Config(("directory", bad))));

            Assert.Equal("INVALID_CONNECTION_CONFIG", error.ErrorCode);
        }
    }

    [Fact]
    public void Database_nao_aceita_configuracao_nenhuma()
    {
        StorageConnectionConfig.Validate(StorageProvider.Database, null);

        Assert.Throws<RequestValidationException>(() =>
            StorageConnectionConfig.Validate(StorageProvider.Database, Config(("directory", "x"))));
    }

    [Fact]
    public void Azure_e_s3_exigem_as_chaves_obrigatorias_e_recusam_as_desconhecidas()
    {
        StorageConnectionConfig.Validate(StorageProvider.AzureBlobStorage, Config(("connectionString", "UseDevelopmentStorage=true"), ("container", "docs")));
        StorageConnectionConfig.Validate(
            StorageProvider.AwsS3, Config(("bucket", "b"), ("accessKeyId", "k"), ("secretAccessKey", "s"), ("region", "sa-east-1")));

        Assert.Throws<RequestValidationException>(() => StorageConnectionConfig.Validate(StorageProvider.AzureBlobStorage, Config(("container", "docs"))));
        Assert.Throws<RequestValidationException>(() => StorageConnectionConfig.Validate(StorageProvider.AwsS3, Config(("bucket", "b"))));
        Assert.Throws<RequestValidationException>(() =>
            StorageConnectionConfig.Validate(StorageProvider.AzureBlobStorage, Config(("connectionString", "x"), ("container", "y"), ("extra", "z"))));
    }

    [Fact]
    public void Valor_nao_texto_ou_vazio_e_recusado()
    {
        var notString = new JsonObject { ["directory"] = 5 };

        Assert.Throws<RequestValidationException>(() => StorageConnectionConfig.Validate(StorageProvider.FileSystem, notString));
        Assert.Throws<RequestValidationException>(() => StorageConnectionConfig.Validate(StorageProvider.FileSystem, Config(("directory", " "))));
        Assert.Throws<RequestValidationException>(() => StorageConnectionConfig.Validate(StorageProvider.FileSystem, Config(("directory", null))));
    }

    [Fact]
    public void A_mensagem_de_erro_diz_o_que_o_provedor_aceita_sem_repetir_o_valor_enviado()
    {
        var error = Assert.Throws<RequestValidationException>(() =>
            StorageConnectionConfig.Validate(StorageProvider.AwsS3, Config(("bucket", "b"), ("secretAccessKey", "SEGREDO-123"))));

        Assert.Contains("accessKeyId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SEGREDO-123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atualizacao_parcial_define_remove_e_preserva()
    {
        var current = Config(("bucket", "b"), ("accessKeyId", "k"), ("secretAccessKey", "s"), ("region", "us-east-1"));

        var merged = StorageConnectionConfig.Merge(current, Config(("secretAccessKey", "novo"), ("region", null), ("serviceUrl", "http://minio:9000")));

        Assert.Equal("novo", (string?)merged["secretAccessKey"]);
        Assert.Equal("k", (string?)merged["accessKeyId"]);
        Assert.False(merged.ContainsKey("region"));
        Assert.Equal("http://minio:9000", (string?)merged["serviceUrl"]);
        Assert.Equal("s", (string?)current["secretAccessKey"]);
    }

    // ---- criação e o padrão único ------------------------------------------------------------------------------

    [Fact]
    public async Task Criar_cifra_a_configuracao_e_normaliza_o_codigo()
    {
        var created = await CreateAsync("arquivo-fs", StorageProvider.FileSystem, Config(("directory", "clientes/acme")));

        Assert.Equal("ARQUIVO-FS", created.Code);
        Assert.NotNull(created.EncryptedConnectionConfig);
        Assert.DoesNotContain("clientes/acme", created.EncryptedConnectionConfig, StringComparison.Ordinal);
        Assert.Equal("clientes/acme", (string?)StoredConfig(created)["directory"]);
        Assert.False(created.IsDefault);
    }

    [Fact]
    public async Task Sem_configuracao_nada_e_guardado()
    {
        var created = await CreateAsync("db");

        Assert.Null(created.EncryptedConnectionConfig);
    }

    [Fact]
    public async Task Criar_como_padrao_troca_o_padrao_e_continua_havendo_exatamente_um()
    {
        var created = await CreateAsync("novo-padrao", isDefault: true);

        Assert.True(created.IsDefault);
        Assert.Single(_store.Items, repository => repository.IsDefault);
        Assert.Equal(created.Id, _store.Default.Id);
        Assert.False(_store.Items.Single(repository => repository.Id == StorageRepository.DefaultRepositoryId).IsDefault);
    }

    [Fact]
    public async Task Codigo_repetido_ignorando_caixa_e_conflito()
    {
        await CreateAsync("db");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => CreateAsync("DB"));

        Assert.Equal("STORAGE_REPOSITORY_CODE_EXISTS", error.ErrorCode);
    }

    [Theory]
    [InlineData("com espaco", "Nome", "INVALID_STORAGE_REPOSITORY_CODE")]
    [InlineData("", "Nome", "INVALID_STORAGE_REPOSITORY_CODE")]
    [InlineData("OK", "", "INVALID_STORAGE_REPOSITORY_NAME")]
    public async Task Dados_invalidos_sao_erro_de_validacao(string code, string name, string errorCode)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync(code, name, StorageProvider.Database, null, false, true, Ct));

        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Theory]
    [InlineData(StorageProvider.AzureBlobStorage)]
    [InlineData(StorageProvider.AwsS3)]
    public async Task Provedor_nao_implementado_nao_pode_ser_o_padrao_mas_pode_ser_criado(StorageProvider provider)
    {
        var config = provider == StorageProvider.AzureBlobStorage
            ? Config(("connectionString", "x"), ("container", "y"))
            : Config(("bucket", "b"), ("accessKeyId", "k"), ("secretAccessKey", "s"));

        var created = await CreateAsync("nuvem", provider, config);
        var error = await Assert.ThrowsAsync<UnprocessableRequestException>(() => CreateAsync("nuvem-padrao", provider, config, isDefault: true));

        Assert.False(created.IsImplemented);
        Assert.Equal("STORAGE_PROVIDER_NOT_IMPLEMENTED", error.ErrorCode);
        Assert.Equal(StorageRepository.DefaultRepositoryId, _store.Default.Id);
    }

    [Fact]
    public async Task Repositorio_inativo_nao_pode_ser_criado_como_padrao()
    {
        var error = await Assert.ThrowsAsync<UnprocessableRequestException>(() => CreateAsync("off", isDefault: true, active: false));

        Assert.Equal("STORAGE_REPOSITORY_INACTIVE", error.ErrorCode);
    }

    // ---- atualização ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Atualizar_muda_nome_e_ativo()
    {
        var created = await CreateAsync("db");

        var updated = await Service().UpdateAsync(created.Id, "Outro nome", false, null, null, Ct);

        Assert.Equal("Outro nome", updated.Name);
        Assert.False(updated.Active);
    }

    [Fact]
    public async Task Tornar_outro_repositorio_padrao_desliga_o_anterior()
    {
        var created = await CreateAsync("db");

        await Service().UpdateAsync(created.Id, "db", true, isDefault: true, null, Ct);

        Assert.Equal(created.Id, _store.Default.Id);
        Assert.Single(_store.Items, repository => repository.IsDefault);
    }

    [Fact]
    public async Task O_padrao_atual_nao_pode_deixar_de_ser_padrao_nem_ser_desativado()
    {
        var current = _store.Default;

        var off = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().UpdateAsync(current.Id, "x", true, isDefault: false, null, Ct));
        var inactive = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().UpdateAsync(current.Id, "x", false, null, null, Ct));

        Assert.Equal("DEFAULT_STORAGE_REPOSITORY_REQUIRED", off.ErrorCode);
        Assert.Equal("DEFAULT_STORAGE_REPOSITORY_REQUIRED", inactive.ErrorCode);
        Assert.True(current.IsDefault);
    }

    [Fact]
    public async Task Repetir_isdefault_true_no_padrao_e_permitido_e_nao_muda_nada()
    {
        var current = _store.Default;

        await Service().UpdateAsync(current.Id, "Novo nome", true, isDefault: true, null, Ct);

        Assert.Equal(current.Id, _store.Default.Id);
        Assert.Equal("Novo nome", current.Name);
    }

    [Fact]
    public async Task Tornar_padrao_um_repositorio_inativo_ou_nao_implementado_e_422()
    {
        var inactive = await CreateAsync("off", active: false);
        var cloud = await CreateAsync("azure", StorageProvider.AzureBlobStorage, Config(("connectionString", "x"), ("container", "y")));

        await Assert.ThrowsAsync<UnprocessableRequestException>(() => Service().UpdateAsync(inactive.Id, "off", false, isDefault: true, null, Ct));
        await Assert.ThrowsAsync<UnprocessableRequestException>(() => Service().UpdateAsync(cloud.Id, "azure", true, isDefault: true, null, Ct));
        Assert.Equal(StorageRepository.DefaultRepositoryId, _store.Default.Id);
    }

    [Fact]
    public async Task Configuracao_parcial_preserva_o_que_nao_foi_enviado()
    {
        var created = await CreateAsync(
            "s3", StorageProvider.AwsS3, Config(("bucket", "b"), ("accessKeyId", "k"), ("secretAccessKey", "s"), ("region", "us-east-1")));

        var updated = await Service().UpdateAsync(created.Id, "s3", true, null, Config(("secretAccessKey", "novo"), ("region", null)), Ct);

        var stored = StoredConfig(updated);
        Assert.Equal("novo", (string?)stored["secretAccessKey"]);
        Assert.Equal("k", (string?)stored["accessKeyId"]);
        Assert.Equal("b", (string?)stored["bucket"]);
        Assert.False(stored.ContainsKey("region"));
    }

    [Fact]
    public async Task Sem_configuracao_no_pedido_a_guardada_fica_intacta()
    {
        var created = await CreateAsync("fs", StorageProvider.FileSystem, Config(("directory", "a/b")));
        var before = created.EncryptedConnectionConfig;

        await Service().UpdateAsync(created.Id, "novo nome", true, null, null, Ct);
        await Service().UpdateAsync(created.Id, "novo nome", true, null, [], Ct);

        Assert.Equal(before, created.EncryptedConnectionConfig);
    }

    [Fact]
    public async Task Configuracao_parcial_invalida_para_o_provedor_e_recusada_e_nada_muda()
    {
        var created = await CreateAsync("s3", StorageProvider.AwsS3, Config(("bucket", "b"), ("accessKeyId", "k"), ("secretAccessKey", "s")));
        var before = created.EncryptedConnectionConfig;

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().UpdateAsync(created.Id, "s3", true, null, Config(("bucket", null)), Ct));

        Assert.Equal(before, created.EncryptedConnectionConfig);
    }

    [Fact]
    public async Task O_diretorio_nao_pode_mudar_depois_que_ha_documentos_mas_pode_antes()
    {
        var created = await CreateAsync("fs", StorageProvider.FileSystem, Config(("directory", "a")));

        await Service().UpdateAsync(created.Id, "fs", true, null, Config(("directory", "b")), Ct);
        Assert.Equal("b", (string?)StoredConfig(created)["directory"]);

        _store.WithDocuments.Add(created.Id);
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Service().UpdateAsync(created.Id, "fs", true, null, Config(("directory", "c")), Ct));

        Assert.Equal("STORAGE_REPOSITORY_IN_USE", error.ErrorCode);
        Assert.Equal("b", (string?)StoredConfig(created)["directory"]);
    }

    // ---- exclusão -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task O_padrao_nao_pode_ser_excluido()
    {
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(StorageRepository.DefaultRepositoryId, Ct));

        Assert.Equal("DEFAULT_STORAGE_REPOSITORY_REQUIRED", error.ErrorCode);
    }

    [Fact]
    public async Task Repositorio_com_documentos_ou_usado_por_produto_nao_pode_ser_excluido()
    {
        var withDocuments = await CreateAsync("com-docs");
        var withProduct = await CreateAsync("com-produto");
        _store.WithDocuments.Add(withDocuments.Id);
        _store.UsedByProducts.Add(withProduct.Id);

        await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(withDocuments.Id, Ct));
        await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(withProduct.Id, Ct));
    }

    [Fact]
    public async Task Repositorio_sem_uso_e_excluido()
    {
        var created = await CreateAsync("livre");

        await Service().DeleteAsync(created.Id, Ct);

        Assert.DoesNotContain(_store.Items, repository => repository.Id == created.Id);
    }

    [Fact]
    public async Task Repositorio_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().GetAsync(Guid.NewGuid(), Ct));

        Assert.Equal("storage-repository", error.Resource);
    }

    // ---- produto ---------------------------------------------------------------------------------------------------

    private ProductServiceService Products(InMemoryProductServiceStore products) =>
        new(products, _store, _clock, NullLogger<ProductServiceService>.Instance);

    [Fact]
    public async Task Produto_pode_nomear_um_repositorio_existente_e_ativo_ou_nenhum()
    {
        var products = new InMemoryProductServiceStore();
        var repository = await CreateAsync("db");

        var withRepository = await Products(products).CreateAsync("p1", "P1", true, repository.Id, Ct);
        var without = await Products(products).CreateAsync("p2", "P2", true, null, Ct);

        Assert.Equal(repository.Id, withRepository.StorageRepositoryId);
        Assert.Null(without.StorageRepositoryId);
    }

    [Fact]
    public async Task Produto_com_repositorio_inexistente_ou_inativo_e_422()
    {
        var products = new InMemoryProductServiceStore();
        var inactive = await CreateAsync("off", active: false);

        var missing = await Assert.ThrowsAsync<UnprocessableRequestException>(() => Products(products).CreateAsync("p1", "P1", true, Guid.NewGuid(), Ct));
        var off = await Assert.ThrowsAsync<UnprocessableRequestException>(() => Products(products).CreateAsync("p2", "P2", true, inactive.Id, Ct));

        Assert.Equal("STORAGE_REPOSITORY_NOT_FOUND", missing.ErrorCode);
        Assert.Equal("STORAGE_REPOSITORY_INACTIVE", off.ErrorCode);
        Assert.Empty(products.Items);
    }

    [Fact]
    public async Task Atualizar_o_produto_troca_o_repositorio_e_null_volta_ao_padrao()
    {
        var products = new InMemoryProductServiceStore();
        var repository = await CreateAsync("db");
        var product = await Products(products).CreateAsync("p1", "P1", true, null, Ct);

        await Products(products).UpdateAsync(product.Id, "P1", true, repository.Id, Ct);
        Assert.Equal(repository.Id, product.StorageRepositoryId);

        await Products(products).UpdateAsync(product.Id, "P1", true, null, Ct);
        Assert.Null(product.StorageRepositoryId);
    }

    // ---- escolha no upload ---------------------------------------------------------------------------------------

    private readonly InMemoryProductServiceStore _productStore = new();
    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly InMemoryFileStorage _files = new();

    private DocumentUploadService Upload() => new(
        _files,
        _documents,
        _productStore,
        new RetentionService(new InMemoryRetentionPolicyStore()),
        new StorageRepositoryResolver(_store),
        _documents,
        new SequentialProtocolGenerator(),
        new DocumentPageCounter(),
        Options.Create(new UploadOptions()),
        Options.Create(new IdempotencyOptions()),
        _clock,
        NullLogger<DocumentUploadService>.Instance);

    private async Task<Document> UploadAsync(string? productCode = null)
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);
        await Upload().UploadAsync(
            new UploadDocumentCommand(content, "doc.pdf", "application/pdf", null, null, UploadChannel.Api, null, productCode),
            Ct);

        return _documents.Documents.Last();
    }

    [Fact]
    public async Task Upload_sem_produto_vai_para_o_repositorio_padrao_e_o_documento_guarda_qual()
    {
        var document = await UploadAsync();

        Assert.Equal(StorageRepository.DefaultRepositoryId, document.StorageRepositoryId);
        Assert.Equal([StorageRepository.DefaultRepositoryId], _files.SavedIn);
    }

    [Fact]
    public async Task Upload_de_produto_com_repositorio_vai_para_esse_repositorio()
    {
        var repository = await CreateAsync("db");
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, repository.Id, Ct);

        var document = await UploadAsync("conta-pj");

        Assert.Equal(repository.Id, document.StorageRepositoryId);
        Assert.Equal([repository.Id], _files.SavedIn);
    }

    [Fact]
    public async Task Upload_de_produto_sem_repositorio_usa_o_padrao_de_agora()
    {
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, null, Ct);
        var newDefault = await CreateAsync("novo-padrao", isDefault: true);

        var document = await UploadAsync("conta-pj");

        Assert.Equal(newDefault.Id, document.StorageRepositoryId);
    }

    [Fact]
    public async Task Trocar_o_padrao_depois_nao_muda_onde_o_documento_ja_esta()
    {
        var before = await UploadAsync();
        await CreateAsync("novo-padrao", isDefault: true);
        var after = await UploadAsync();

        Assert.Equal(StorageRepository.DefaultRepositoryId, before.StorageRepositoryId);
        Assert.NotEqual(before.StorageRepositoryId, after.StorageRepositoryId);
    }

    [Fact]
    public async Task Repositorio_do_produto_desativado_depois_recusa_o_upload_com_422_e_nada_e_gravado()
    {
        var repository = await CreateAsync("db");
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, repository.Id, Ct);
        await Service().UpdateAsync(repository.Id, "db", false, null, null, Ct);

        await using var content = Samples.StreamOf(Samples.ThreePagePdf);
        var error = await Assert.ThrowsAsync<UploadRejectedException>(() => Upload().UploadAsync(
            new UploadDocumentCommand(content, "doc.pdf", "application/pdf", null, null, UploadChannel.Api, null, "conta-pj"), Ct));

        Assert.Equal("STORAGE_REPOSITORY_INACTIVE", error.ErrorCode);
        Assert.Equal(UploadRejectionReason.UnprocessableContent, error.Reason);
        Assert.Empty(_documents.Documents);
        Assert.Empty(_files.Blobs);
    }

    [Fact]
    public async Task Se_a_persistencia_falha_o_arquivo_orfao_e_removido_do_mesmo_repositorio()
    {
        var repository = await CreateAsync("db");
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, repository.Id, Ct);
        _documents.AcceptFailure = new InvalidOperationException("banco fora");

        await using var content = Samples.StreamOf(Samples.ThreePagePdf);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload().UploadAsync(
            new UploadDocumentCommand(content, "doc.pdf", "application/pdf", null, null, UploadChannel.Api, null, "conta-pj"), Ct));

        Assert.Equal([repository.Id], _files.DeletedFrom);
        Assert.Empty(_files.Blobs);
    }

    // ---- leitura e exclusão pelo repositório do documento -----------------------------------------------------

    [Fact]
    public async Task Ler_o_conteudo_vai_ao_repositorio_do_documento()
    {
        var repository = await CreateAsync("db");
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, repository.Id, Ct);
        var document = await UploadAsync("conta-pj");
        document.MarkCompleted(Now);

        var query = new DocumentQueryService(
            _documents, _files, new DocReader.Application.Classification.RulesDocumentClassifier(), [], NullLogger<DocumentQueryService>.Instance);
        await using var content = await query.OpenContentAsync(document.Id, Ct);

        Assert.Equal([repository.Id], _files.ReadFrom);
    }

    [Fact]
    public async Task Excluir_o_documento_remove_o_arquivo_do_repositorio_dele()
    {
        var repository = await CreateAsync("db");
        await Products(_productStore).CreateAsync("conta-pj", "Conta PJ", true, repository.Id, Ct);
        var document = await UploadAsync("conta-pj");

        await new DocumentDeletionService(_documents, _files, NullLogger<DocumentDeletionService>.Instance).DeleteAsync(document.Id, Ct);

        Assert.Equal([repository.Id], _files.DeletedFrom);
        Assert.Empty(_files.Blobs);
    }

    // ---- fachada ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fachada_escolhe_o_adapter_pelo_provedor_do_repositorio_e_entrega_a_configuracao_decifrada()
    {
        var factory = new RecordingAdapterFactory();
        var database = await CreateAsync("db");
        var fileSystem = await CreateAsync("fs", StorageProvider.FileSystem, Config(("directory", "clientes/acme")));
        var facade = new RepositoryFileStorage(_store, factory, _protector);
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        await using (var one = new MemoryStream([1, 2, 3]))
        {
            await facade.SaveAsync(database.Id, one, metadata, Ct);
        }

        await using (var two = new MemoryStream([4, 5]))
        {
            await facade.SaveAsync(fileSystem.Id, two, metadata, Ct);
        }

        Assert.Equal([StorageProvider.Database, StorageProvider.FileSystem], factory.Created.Select(adapter => adapter.Provider));
        Assert.Null(factory.Created[0].Config);
        Assert.Equal("clientes/acme", (string?)factory.Created[1].Config!["directory"]);
    }

    [Fact]
    public async Task A_fachada_le_e_apaga_no_repositorio_pedido()
    {
        var factory = new RecordingAdapterFactory();
        var facade = new RepositoryFileStorage(_store, factory, _protector);
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        await using var content = new MemoryStream([9, 9]);
        var stored = await facade.SaveAsync(StorageRepository.DefaultRepositoryId, content, metadata, Ct);
        await using var read = await facade.OpenReadAsync(StorageRepository.DefaultRepositoryId, stored.StorageKey, Ct);

        Assert.Equal(2, read.Length);
        await facade.DeleteAsync(StorageRepository.DefaultRepositoryId, stored.StorageKey, Ct);
        Assert.All(factory.Created, adapter => Assert.Empty(adapter.Blobs));
    }

    [Fact]
    public async Task A_fachada_com_repositorio_inexistente_falha_com_mensagem_clara()
    {
        var facade = new RepositoryFileStorage(_store, new RecordingAdapterFactory(), _protector);

        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.OpenReadAsync(Guid.NewGuid(), "x", Ct));
    }

    [Fact]
    public async Task O_estado_de_gravacao_e_o_do_repositorio_padrao()
    {
        var factory = new RecordingAdapterFactory();
        var facade = new RepositoryFileStorage(_store, factory, _protector);

        Assert.True(await facade.IsWritableAsync(Ct));
        Assert.Equal(StorageProvider.FileSystem, factory.Created.Single().Provider);
    }

    [Fact]
    public async Task Configuracao_ilegivel_com_outra_chave_falha_sem_vazar_o_conteudo()
    {
        var repository = await CreateAsync("fs", StorageProvider.FileSystem, Config(("directory", "a")));
        repository.ReplaceConnectionConfig("{\"outra\":\"chave\"}", Now);
        var facade = new RepositoryFileStorage(_store, new RecordingAdapterFactory(), _protector);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => facade.OpenReadAsync(repository.Id, "x", Ct));

        Assert.DoesNotContain("chave", error.Message, StringComparison.Ordinal);
    }
}
