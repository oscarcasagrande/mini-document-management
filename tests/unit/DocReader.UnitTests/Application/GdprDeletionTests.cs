using DocReader.Application.Audit;
using DocReader.Application.Catalog;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Extraction;
using DocReader.Application.GdprDeletion;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.GdprDeletion;
using DocReader.Domain.Retention;
using DocReader.Infrastructure.Files;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// The GDPR/LGPD deletion workflow: the validation before a request is accepted, the PENDING/APPROVED/
/// REJECTED/EXECUTED state machine, the 24-hour auto-approve, what execution removes and leaves, the 410 gate
/// afterwards, and that every transition is recorded on the document's timeline and in the audit log.
/// </summary>
public sealed class GdprDeletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRetentionPolicyStore _policies = new();
    private readonly InMemoryProductServiceStore _products = new();
    private readonly InMemoryStorageRepositoryStore _storageRepositories = new();
    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly InMemoryFileStorage _storage = new();
    private readonly InMemoryAuditLogStore _auditLogStore = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private InMemoryGdprDeletionRequestStore Requests() => new(_documents);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RetentionPolicyService Policies() =>
        new(_policies, _products, InMemoryDocumentTypeStore.WithBuiltIns(), _clock, NullLogger<RetentionPolicyService>.Instance);

    private GdprDeletionRequestService RequestService(InMemoryGdprDeletionRequestStore store) => new(
        store, _documents, _products, new AuditLogService(_auditLogStore, _clock), _clock, NullLogger<GdprDeletionRequestService>.Instance);

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

    private async Task<Document> UploadAsync(string? productCode = null)
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);
        await Upload().UploadAsync(
            new UploadDocumentCommand(content, "doc.pdf", "application/pdf", null, null, UploadChannel.Api, null, productCode),
            Ct);

        return _documents.Documents.Last();
    }

    private static readonly ExtractionSummary Summary = new(
        Guid.NewGuid(), "paddleocr", "PP-OCRv5 test", "rules-2.0.0", "br-cnh-1.1.0", 1, 0.9m, false, null, false, false, Now);

    /// <summary>An uploaded, completed document whose retention already expired, with OCR text and one extracted field.</summary>
    private async Task<Document> EligibleDocumentAsync(int retentionDays = 10, string? productCode = null)
    {
        await Policies().UpdateAsync(RetentionPolicy.GlobalPolicyId, retentionDays, Ct);
        var document = await UploadAsync(productCode);
        document.RecordClassification("BR_CNH", 0.9m, Now);
        document.MarkCompleted(Now);
        _documents.Jobs.Clear();

        _documents.Extractions[document.Id] = (
            new ExtractionResultView(Summary, [new ExtractedFieldView("cpf", "123.456.789-09", "12345678909", 0.99m, 1, "{}", "VALID", "[]")]),
            new ExtractionTextView(Summary, "[{\"pageNumber\":1,\"text\":\"CPF 123.456.789-09\"}]"));
        _documents.RawOcr[document.Id] = "{\"raw\":\"CPF 123.456.789-09\"}";

        return document;
    }

    private DocumentQueryService Queries() =>
        new(_documents, _storage, new RulesDocumentClassifier(), [], NullLogger<DocumentQueryService>.Instance);

    // ---- validação antes de aceitar o pedido --------------------------------------------------------------

    [Fact]
    public async Task Documento_vinculado_a_produto_ativo_e_conflito()
    {
        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        await _products.AddAsync(product, Ct);
        var document = await EligibleDocumentAsync(productCode: "conta-pj");

        var later = new FakeTimeProvider(Now.AddDays(11));
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            RequestServiceAt(later).RequestDeletionAsync(document.Id, null, null, null, null, Ct));

        Assert.Equal("DOCUMENT_IN_USE_BY_PRODUCT_SERVICE", error.ErrorCode);
        Assert.Contains("Conta PJ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Produto_desativado_depois_do_upload_nao_bloqueia_mais()
    {
        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        await _products.AddAsync(product, Ct);
        var document = await EligibleDocumentAsync(productCode: "conta-pj");
        product.Update(product.Name, active: false, Now);

        var later = new FakeTimeProvider(Now.AddDays(11));
        var request = await RequestServiceAt(later).RequestDeletionAsync(document.Id, null, null, null, null, Ct);

        Assert.Equal(GdprDeletionRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task Retencao_ainda_nao_vencida_e_erro_de_validacao()
    {
        var document = await EligibleDocumentAsync(retentionDays: 30);

        // Still within the 30-day window at Now itself.
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            RequestService(Requests()).RequestDeletionAsync(document.Id, null, null, null, null, Ct));

        Assert.Equal("RETENTION_NOT_EXPIRED", error.ErrorCode);
        Assert.Contains(document.ExpiresAt!.Value.ToString("yyyy-MM-dd"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retencao_ja_vencida_e_aceita_mesmo_que_o_expurgo_ainda_nao_tenha_rodado()
    {
        var document = await EligibleDocumentAsync(retentionDays: 10);

        var later = new FakeTimeProvider(Now.AddDays(11));
        var request = await RequestServiceAt(later).RequestDeletionAsync(document.Id, null, "titular pediu", null, null, Ct);

        Assert.Equal(GdprDeletionRequestStatus.Pending, request.Status);
        Assert.Equal("titular pediu", request.Reason);
    }

    [Fact]
    public async Task Documento_inexistente_e_not_found()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            RequestService(Requests()).RequestDeletionAsync(Guid.NewGuid(), null, null, null, null, Ct));
    }

    private GdprDeletionRequestService RequestServiceAt(FakeTimeProvider clock) => new(
        Requests(), _documents, _products, new AuditLogService(_auditLogStore, clock), clock, NullLogger<GdprDeletionRequestService>.Instance);

    // ---- pedido, aprovação, rejeição e execução -----------------------------------------------------------

    [Fact]
    public async Task Pedido_eligivel_fica_pendente_grava_a_linha_do_tempo_e_a_auditoria()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();

        var request = await RequestServiceAt(requests, later).RequestDeletionAsync(document.Id, "alice", "titular pediu", "10.0.0.5", "curl/8", Ct);

        Assert.Equal(GdprDeletionRequestStatus.Pending, request.Status);
        Assert.Equal(document.Id, request.DocumentId);
        Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionRequested);

        var audit = Assert.Single(_auditLogStore.Entries);
        Assert.Equal("GDPR_DELETION_REQUESTED", audit.Action);
        Assert.Equal("document", audit.ResourceType);
        Assert.Equal(document.Id.ToString(), audit.ResourceId);
        Assert.Equal("alice", audit.UserId);
        Assert.Equal("10.0.0.5", audit.IpAddress);
        Assert.DoesNotContain("titular pediu", audit.Changes ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aprovar_e_executar_remove_arquivo_texto_e_campos_e_marca_executed()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        var requestService = RequestServiceAt(requests, later);
        var request = await requestService.RequestDeletionAsync(document.Id, null, null, null, null, Ct);
        var key = document.StorageKey;

        var approved = await requestService.ApproveAsync(request.Id, "bob", "10.0.0.6", "curl/8", Ct);
        Assert.Equal(GdprDeletionRequestStatus.Approved, approved.Status);
        Assert.Equal("bob", approved.ApprovedBy);
        Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionApproved);

        var executed = await ExecutionServiceAt(requests, later).ExecuteNextApprovedAsync(Ct);

        Assert.True(executed);
        Assert.Equal(DocumentStatus.Purged, document.Status);
        Assert.DoesNotContain(key, _storage.Blobs.Keys);
        Assert.False(_documents.Extractions.ContainsKey(document.Id));

        var executedEvent = Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionExecuted);
        Assert.Equal("reason=GDPR_REQUEST", executedEvent.Details!.Split(' ')[0]);
        Assert.Contains("deleted=file,ocr_text,extracted_fields", executedEvent.Details, StringComparison.Ordinal);

        var reloaded = await requests.FindByIdAsync(request.Id, Ct);
        Assert.Equal(GdprDeletionRequestStatus.Executed, reloaded!.Status);
        Assert.NotNull(reloaded.ExecutedAt);

        Assert.Contains(_auditLogStore.Entries, entry => entry.Action == "GDPR_DELETION_APPROVED");
        Assert.Contains(_auditLogStore.Entries, entry => entry.Action == "GDPR_DELETION_EXECUTED");
    }

    [Fact]
    public async Task Rejeitar_nao_apaga_nada_e_fecha_o_pedido()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        var requestService = RequestServiceAt(requests, later);
        var request = await requestService.RequestDeletionAsync(document.Id, null, null, null, null, Ct);

        var rejected = await requestService.RejectAsync(request.Id, "bob", null, null, Ct);

        Assert.Equal(GdprDeletionRequestStatus.Rejected, rejected.Status);
        Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionRejected);
        Assert.Contains(document.StorageKey, _storage.Blobs.Keys);
        Assert.Equal(DocumentStatus.Completed, document.Status);

        var executedAgain = await ExecutionServiceAt(requests, later).ExecuteNextApprovedAsync(Ct);
        Assert.False(executedAgain);
    }

    [Fact]
    public async Task Aprovar_pedido_que_nao_esta_pendente_e_conflito()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        var requestService = RequestServiceAt(requests, later);
        var request = await requestService.RequestDeletionAsync(document.Id, null, null, null, null, Ct);
        await requestService.RejectAsync(request.Id, "bob", null, null, Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            requestService.ApproveAsync(request.Id, "carol", null, null, Ct));

        Assert.Equal("GDPR_DELETION_REQUEST_NOT_PENDING", error.ErrorCode);
    }

    [Fact]
    public async Task Pedido_inexistente_e_not_found()
    {
        var requestService = RequestService(Requests());

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => requestService.GetByIdAsync(Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => requestService.ApproveAsync(Guid.NewGuid(), null, null, null, Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => requestService.RejectAsync(Guid.NewGuid(), null, null, null, Ct));
    }

    // ---- aprovação automática após a janela configurada ---------------------------------------------------

    [Fact]
    public async Task Pedido_ainda_pendente_apos_a_janela_e_aprovado_automaticamente_pelo_worker()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        var request = await RequestServiceAt(requests, later).RequestDeletionAsync(document.Id, null, null, null, null, Ct);

        // Before the 24-hour window: nothing to auto-approve yet.
        var tooSoon = new FakeTimeProvider(later.GetUtcNow().AddHours(23));
        Assert.Equal(0, await ExecutionServiceAt(requests, tooSoon).AutoApprovePastDueAsync(Ct));
        Assert.Equal(GdprDeletionRequestStatus.Pending, (await requests.FindByIdAsync(request.Id, Ct))!.Status);

        // Past the window: auto-approved with the sentinel identity, not a real user id.
        var pastDue = new FakeTimeProvider(later.GetUtcNow().AddHours(25));
        Assert.Equal(1, await ExecutionServiceAt(requests, pastDue).AutoApprovePastDueAsync(Ct));

        var autoApproved = await requests.FindByIdAsync(request.Id, Ct);
        Assert.Equal(GdprDeletionRequestStatus.Approved, autoApproved!.Status);
        Assert.Equal(GdprDeletionOptions.AutoApprovedBy, autoApproved.ApprovedBy);
        Assert.Contains(_auditLogStore.Entries, entry =>
            entry.Action == "GDPR_DELETION_APPROVED" && entry.UserId == GdprDeletionOptions.AutoApprovedBy);

        var executed = await ExecutionServiceAt(requests, pastDue).ExecuteNextApprovedAsync(Ct);
        Assert.True(executed);
        Assert.Equal(DocumentStatus.Purged, document.Status);
    }

    [Fact]
    public async Task Janela_de_auto_aprovacao_e_configuravel()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        await RequestServiceAt(requests, later).RequestDeletionAsync(document.Id, null, null, null, null, Ct);

        var oneHourLater = new FakeTimeProvider(later.GetUtcNow().AddHours(1));
        Assert.Equal(1, await ExecutionServiceAt(requests, oneHourLater, autoApproveAfterHours: 0.5).AutoApprovePastDueAsync(Ct));
    }

    private GdprDeletionRequestService RequestServiceAt(InMemoryGdprDeletionRequestStore store, FakeTimeProvider clock) => new(
        store, _documents, _products, new AuditLogService(_auditLogStore, clock), clock, NullLogger<GdprDeletionRequestService>.Instance);

    private GdprDeletionExecutionService ExecutionServiceAt(
        InMemoryGdprDeletionRequestStore store, FakeTimeProvider clock, double autoApproveAfterHours = 24) => new(
        store,
        _storage,
        new AuditLogService(_auditLogStore, clock),
        Options.Create(new GdprDeletionOptions { AutoApproveAfterHours = autoApproveAfterHours }),
        clock,
        NullLogger<GdprDeletionExecutionService>.Instance);

    // ---- 410 depois da execução, 200 continua no documento ------------------------------------------------

    [Fact]
    public async Task Depois_de_executado_content_text_e_result_respondem_gone_mas_o_documento_continua_200()
    {
        var document = await EligibleDocumentAsync();
        var later = new FakeTimeProvider(Now.AddDays(11));
        var requests = Requests();
        var requestService = RequestServiceAt(requests, later);
        var request = await requestService.RequestDeletionAsync(document.Id, null, null, null, null, Ct);
        await requestService.ApproveAsync(request.Id, "bob", null, null, Ct);
        await ExecutionServiceAt(requests, later).ExecuteNextApprovedAsync(Ct);

        var queries = Queries();

        await Assert.ThrowsAsync<DocumentPurgedException>(() => queries.OpenContentAsync(document.Id, Ct));
        await Assert.ThrowsAsync<DocumentPurgedException>(() => queries.GetTextAsync(document.Id, Ct));
        await Assert.ThrowsAsync<DocumentPurgedException>(() => queries.GetResultAsync(document.Id, Ct));
        await Assert.ThrowsAsync<DocumentPurgedException>(() => queries.GetClassificationDiagnosticsAsync(document.Id, Ct));
        await Assert.ThrowsAsync<DocumentPurgedException>(() => queries.GetExtractionDiagnosticsAsync(document.Id, Ct));

        var byId = await queries.GetByIdAsync(document.Id, includeEvents: true, Ct);
        Assert.Equal(DocumentStatus.Purged, byId.Status);
        Assert.Equal(document.Protocol, byId.Protocol);
    }
}
