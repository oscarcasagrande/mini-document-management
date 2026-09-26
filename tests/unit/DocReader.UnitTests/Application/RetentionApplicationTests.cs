using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Retention;
using DocReader.Infrastructure.Files;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>A administração das políticas, a data de expurgo no upload e no reprocessamento, e o job de expurgo.</summary>
public sealed class RetentionApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRetentionPolicyStore _policies = new();
    private readonly InMemoryProductServiceStore _products = new();
    private readonly InMemoryStorageRepositoryStore _storageRepositories = new();
    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly InMemoryFileStorage _storage = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RetentionPolicyService Service() =>
        new(_policies, _products, _clock, NullLogger<RetentionPolicyService>.Instance);

    private async Task<ProductService> AddProductAsync(string code, bool active = true)
    {
        var product = ProductService.Create(Guid.CreateVersion7(Now), code, code, active, Now);
        await _products.AddAsync(product, Ct);

        return product;
    }

    // ---- administração -------------------------------------------------------------------------------------

    [Fact]
    public async Task Criar_politica_por_tipo_normaliza_a_caixa_e_guarda_os_dias()
    {
        var created = await Service().CreateAsync("br_cnh", null, 30, Ct);

        Assert.Equal("BR_CNH", created.DocumentType);
        Assert.Equal(30, created.RetentionDays);
        Assert.Equal(RetentionScope.DocumentType, created.Scope);
    }

    [Fact]
    public async Task Criar_politica_por_produto_exige_que_o_produto_exista()
    {
        var error = await Assert.ThrowsAsync<UnprocessableRequestException>(() =>
            Service().CreateAsync(null, Guid.NewGuid(), 30, Ct));

        Assert.Equal("PRODUCT_SERVICE_NOT_FOUND", error.ErrorCode);
    }

    [Fact]
    public async Task Criar_politica_de_tipo_e_produto_grava_os_dois_eixos()
    {
        var product = await AddProductAsync("CONTA-PJ");

        var created = await Service().CreateAsync("BR_CNPJ_CARD", product.Id, 730, Ct);

        Assert.Equal(RetentionScope.DocumentTypeAndProductService, created.Scope);
        Assert.Equal(product.Id, created.ProductServiceId);
    }

    [Fact]
    public async Task Politica_repetida_para_o_mesmo_tipo_e_produto_e_conflito()
    {
        var product = await AddProductAsync("CONTA-PJ");
        await Service().CreateAsync("BR_CNH", product.Id, 30, Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().CreateAsync("br_cnh", product.Id, 60, Ct));

        Assert.Equal("RETENTION_POLICY_EXISTS", error.ErrorCode);
    }

    [Fact]
    public async Task Mesmo_tipo_com_outro_produto_ou_sem_produto_nao_e_duplicata()
    {
        var a = await AddProductAsync("A");
        var b = await AddProductAsync("B");

        await Service().CreateAsync("BR_CNH", a.Id, 30, Ct);
        await Service().CreateAsync("BR_CNH", b.Id, 30, Ct);
        await Service().CreateAsync("BR_CNH", null, 30, Ct);

        Assert.Equal(4, _policies.Items.Count);
    }

    [Fact]
    public async Task Nao_se_cria_uma_segunda_politica_global()
    {
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().CreateAsync(null, null, 10, Ct));

        Assert.Equal("RETENTION_POLICY_EXISTS", error.ErrorCode);
        Assert.Contains("global", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("BR_PASSAPORTE")]
    [InlineData("UNKNOWN")]
    [InlineData("qualquer coisa")]
    public async Task Tipo_documental_desconhecido_e_erro_de_validacao(string type)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => Service().CreateAsync(type, null, 30, Ct));

        Assert.Equal("INVALID_DOCUMENT_TYPE", error.ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(36_501)]
    public async Task Dias_fora_do_intervalo_sao_erro_de_validacao(int days)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => Service().CreateAsync("BR_CNH", null, days, Ct));

        Assert.Equal("INVALID_RETENTION_DAYS", error.ErrorCode);
    }

    [Fact]
    public async Task Todos_os_tipos_que_o_classificador_produz_sao_aceitos()
    {
        foreach (var type in RetentionPolicyService.KnownDocumentTypes)
        {
            await Service().CreateAsync(type, null, 30, Ct);
        }

        Assert.Equal(RetentionPolicyService.KnownDocumentTypes.Count + 1, _policies.Items.Count);
    }

    [Fact]
    public async Task Atualizar_muda_so_os_dias()
    {
        var created = await Service().CreateAsync("BR_CNH", null, 30, Ct);

        var updated = await Service().UpdateAsync(created.Id, 45, Ct);

        Assert.Equal(45, updated.RetentionDays);
        Assert.Equal("BR_CNH", updated.DocumentType);
    }

    [Fact]
    public async Task A_politica_global_pode_ter_os_dias_alterados()
    {
        var updated = await Service().UpdateAsync(RetentionPolicy.GlobalPolicyId, 90, Ct);

        Assert.Equal(90, updated.RetentionDays);
        Assert.True(updated.IsGlobal);
    }

    [Fact]
    public async Task A_politica_global_nao_pode_ser_excluida()
    {
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(RetentionPolicy.GlobalPolicyId, Ct));

        Assert.Equal("GLOBAL_RETENTION_POLICY_PROTECTED", error.ErrorCode);
        Assert.Contains(_policies.Items, policy => policy.IsGlobal);
    }

    [Fact]
    public async Task Politica_especifica_pode_ser_excluida_e_o_que_sobra_e_a_global()
    {
        var created = await Service().CreateAsync("BR_CNH", null, 30, Ct);

        await Service().DeleteAsync(created.Id, Ct);

        Assert.Single(_policies.Items);
    }

    [Fact]
    public async Task Politica_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().GetAsync(Guid.NewGuid(), Ct));

        Assert.Equal("retention-policy", error.Resource);
    }

    // ---- o prazo no upload e no reprocessamento -----------------------------------------------------------

    private DocumentUploadService Upload() => new(
        _storage,
        _documents,
        _products,
        new RetentionService(_policies),
        new StorageRepositoryResolver(_storageRepositories),
        _documents,
        new SequentialProtocolGenerator(),
        new DocumentPageCounter(),
        Options.Create(new UploadOptions()),
        Options.Create(new IdempotencyOptions()),
        _clock,
        NullLogger<DocumentUploadService>.Instance);

    private async Task<Document> UploadAsync(string? productCode = null, string? expectedType = null)
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);
        await Upload().UploadAsync(
            new UploadDocumentCommand(content, "doc.pdf", "application/pdf", expectedType, null, UploadChannel.Api, null, productCode),
            Ct);

        return _documents.Documents.Last();
    }

    [Fact]
    public async Task Upload_sem_nada_configurado_usa_a_global_de_365_dias()
    {
        var document = await UploadAsync();

        Assert.Equal(Now.AddDays(365), document.ExpiresAt);
        Assert.Equal(RetentionPolicy.GlobalPolicyId, document.RetentionPolicyId);
    }

    [Fact]
    public async Task Upload_do_produto_usa_a_politica_do_produto()
    {
        var product = await AddProductAsync("CONTA-PJ");
        await Service().CreateAsync(null, product.Id, 60, Ct);

        var document = await UploadAsync("conta-pj");

        Assert.Equal(Now.AddDays(60), document.ExpiresAt);
    }

    [Fact]
    public async Task Upload_com_tipo_esperado_usa_a_politica_do_tipo()
    {
        await Service().CreateAsync("BR_CNH", null, 20, Ct);

        var document = await UploadAsync(expectedType: "BR_CNH");

        Assert.Equal(Now.AddDays(20), document.ExpiresAt);
    }

    [Fact]
    public async Task Upload_com_tipo_e_produto_prefere_a_politica_dos_dois()
    {
        var product = await AddProductAsync("CONTA-PJ");
        await Service().CreateAsync("BR_CNH", null, 20, Ct);
        await Service().CreateAsync(null, product.Id, 60, Ct);
        await Service().CreateAsync("BR_CNH", product.Id, 5, Ct);

        var document = await UploadAsync("CONTA-PJ", "BR_CNH");

        Assert.Equal(Now.AddDays(5), document.ExpiresAt);
    }

    [Fact]
    public async Task Mudar_a_politica_nao_recalcula_o_documento_que_ja_existe()
    {
        var document = await UploadAsync();
        var expiresBefore = document.ExpiresAt;

        await Service().UpdateAsync(RetentionPolicy.GlobalPolicyId, 10, Ct);
        await Service().CreateAsync("BR_CNH", null, 1, Ct);

        Assert.Equal(expiresBefore, document.ExpiresAt);
        Assert.Equal(365, document.RetentionDays);
    }

    [Fact]
    public async Task Reprocessar_aplica_a_politica_de_hoje_e_recomeca_a_contagem()
    {
        var document = await UploadAsync();
        document.MarkCompleted(Now);
        _documents.Jobs.Clear();
        await Service().UpdateAsync(RetentionPolicy.GlobalPolicyId, 10, Ct);

        var later = new FakeTimeProvider(Now.AddDays(100));
        var reprocessing = new DocumentReprocessingService(
            _documents, new RetentionService(_policies), later, NullLogger<DocumentReprocessingService>.Instance);

        await reprocessing.ReprocessAsync(document.Id, Ct);

        Assert.Equal(Now.AddDays(110), document.ExpiresAt);
        Assert.Equal(10, document.RetentionDays);
    }

    [Fact]
    public async Task Reprocessar_usa_o_tipo_detectado_para_escolher_a_politica()
    {
        var document = await UploadAsync();
        document.RecordClassification("BR_CNH", 0.9m, Now);
        document.MarkCompleted(Now);
        _documents.Jobs.Clear();
        await Service().CreateAsync("BR_CNH", null, 7, Ct);

        var reprocessing = new DocumentReprocessingService(
            _documents, new RetentionService(_policies), _clock, NullLogger<DocumentReprocessingService>.Instance);
        await reprocessing.ReprocessAsync(document.Id, Ct);

        Assert.Equal(Now.AddDays(7), document.ExpiresAt);
    }

    [Fact]
    public async Task Reprocessar_documento_expurgado_e_gone_e_nao_conflito()
    {
        var document = await UploadAsync();
        document.MarkCompleted(Now);
        document.MarkPurged(Now);

        var reprocessing = new DocumentReprocessingService(
            _documents, new RetentionService(_policies), _clock, NullLogger<DocumentReprocessingService>.Instance);

        await Assert.ThrowsAsync<DocumentPurgedException>(() => reprocessing.ReprocessAsync(document.Id, Ct));
    }

    // ---- job de expurgo ---------------------------------------------------------------------------------------

    private DocumentPurgeService Purge(int batchSize = 100, TimeProvider? clock = null, ILogger<DocumentPurgeService>? logger = null) => new(
        _documents,
        _storage,
        Options.Create(new PurgeOptions { BatchSize = batchSize }),
        clock ?? _clock,
        logger ?? NullLogger<DocumentPurgeService>.Instance);

    private async Task<Document> ExpiredAsync(int days = 10, bool completed = true)
    {
        await Service().UpdateAsync(RetentionPolicy.GlobalPolicyId, days, Ct);
        var document = await UploadAsync();

        if (completed)
        {
            document.MarkCompleted(Now);
        }

        return document;
    }

    [Fact]
    public async Task Expurga_o_documento_vencido_remove_o_arquivo_e_marca_purged()
    {
        var document = await ExpiredAsync();
        var key = document.StorageKey;
        Assert.Contains(key, _storage.Blobs.Keys);

        var result = await Purge(clock: new FakeTimeProvider(Now.AddDays(11))).PurgeExpiredAsync(Ct);

        Assert.Equal(new PurgeResult(1, 0), result);
        Assert.Equal(DocumentStatus.Purged, document.Status);
        Assert.DoesNotContain(key, _storage.Blobs.Keys);
        Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.Purged);
    }

    [Fact]
    public async Task Nao_expurga_o_que_ainda_nao_venceu()
    {
        var document = await ExpiredAsync(days: 10);

        var result = await Purge(clock: new FakeTimeProvider(Now.AddDays(9))).PurgeExpiredAsync(Ct);

        Assert.Equal(new PurgeResult(0, 0), result);
        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Contains(document.StorageKey, _storage.Blobs.Keys);
    }

    [Fact]
    public async Task Nao_expurga_documento_em_andamento_mesmo_vencido()
    {
        var document = await ExpiredAsync(completed: false);

        var result = await Purge(clock: new FakeTimeProvider(Now.AddDays(11))).PurgeExpiredAsync(Ct);

        Assert.Equal(0, result.Purged);
        Assert.Equal(DocumentStatus.Queued, document.Status);
        Assert.Contains(document.StorageKey, _storage.Blobs.Keys);
    }

    [Fact]
    public async Task Rodar_duas_vezes_e_idempotente()
    {
        await ExpiredAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));

        var first = await Purge(clock: later).PurgeExpiredAsync(Ct);
        var second = await Purge(clock: later).PurgeExpiredAsync(Ct);

        Assert.Equal(1, first.Purged);
        Assert.Equal(new PurgeResult(0, 0), second);
    }

    [Fact]
    public async Task Documento_ja_expurgado_nao_e_tocado_de_novo()
    {
        var document = await ExpiredAsync();
        document.MarkPurged(Now.AddDays(1));
        var purgedAt = document.PurgedAt;

        var result = await Purge(clock: new FakeTimeProvider(Now.AddDays(30))).PurgeExpiredAsync(Ct);

        Assert.Equal(new PurgeResult(0, 0), result);
        Assert.Equal(purgedAt, document.PurgedAt);
        Assert.Empty(_storage.DeletedKeys);
    }

    [Fact]
    public async Task Varre_em_lotes_ate_esgotar()
    {
        for (var index = 0; index < 5; index++)
        {
            await ExpiredAsync();
        }

        var result = await Purge(batchSize: 2, clock: new FakeTimeProvider(Now.AddDays(11))).PurgeExpiredAsync(Ct);

        Assert.Equal(5, result.Purged);
        Assert.All(_documents.Documents, document => Assert.Equal(DocumentStatus.Purged, document.Status));
    }

    [Fact]
    public async Task Falha_num_arquivo_e_contada_nao_para_os_demais_e_nao_marca_o_documento()
    {
        var bad = await ExpiredAsync();
        var good = await ExpiredAsync();
        var failing = new FailingStorage(_storage, bad.StorageKey);
        var service = new DocumentPurgeService(
            _documents,
            failing,
            Options.Create(new PurgeOptions()),
            new FakeTimeProvider(Now.AddDays(11)),
            NullLogger<DocumentPurgeService>.Instance);

        var result = await service.PurgeExpiredAsync(Ct);

        Assert.Equal(new PurgeResult(1, 1), result);
        Assert.Equal(DocumentStatus.Completed, bad.Status);
        Assert.Equal(DocumentStatus.Purged, good.Status);
    }

    [Fact]
    public async Task O_documento_que_falhou_e_tentado_de_novo_na_proxima_rodada()
    {
        var bad = await ExpiredAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var failing = new DocumentPurgeService(
            _documents, new FailingStorage(_storage, bad.StorageKey), Options.Create(new PurgeOptions()), later, NullLogger<DocumentPurgeService>.Instance);

        await failing.PurgeExpiredAsync(Ct);
        var retry = await Purge(clock: later).PurgeExpiredAsync(Ct);

        Assert.Equal(1, retry.Purged);
        Assert.Equal(DocumentStatus.Purged, bad.Status);
    }

    [Fact]
    public async Task O_log_traz_a_contagem_e_nunca_o_conteudo_nem_o_nome_do_arquivo()
    {
        var document = await ExpiredAsync();
        var logger = new CapturingLogger<DocumentPurgeService>();

        await Purge(clock: new FakeTimeProvider(Now.AddDays(11)), logger: logger).PurgeExpiredAsync(Ct);

        var text = string.Join('\n', logger.Messages);
        Assert.Contains("purged=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain(document.OriginalFileName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Sha256, text, StringComparison.Ordinal);
    }

    private sealed class FailingStorage(InMemoryFileStorage inner, string failingKey) : DocReader.Application.Abstractions.IFileStorage
    {
        public Task<DocReader.Application.Abstractions.StoredFile> SaveAsync(
            Guid repositoryId, Stream content, DocReader.Application.Abstractions.FileMetadata metadata, CancellationToken ct) =>
            inner.SaveAsync(repositoryId, content, metadata, ct);

        public Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct) =>
            inner.OpenReadAsync(repositoryId, storageKey, ct);

        public Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct) =>
            storageKey == failingKey ? throw new IOException("disk on fire") : inner.DeleteAsync(repositoryId, storageKey, ct);

        public Task<bool> IsWritableAsync(CancellationToken ct) => inner.IsWritableAsync(ct);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + " " + string.Join(' ', (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(pair => $"{pair.Key}={pair.Value}") ?? []));
    }
}
