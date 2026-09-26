using DocReader.Application.Errors;
using DocReader.Application.Documents;
using DocReader.Application.Retention;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Retention against a real PostgreSQL: the global policy the migration seeds, the uniqueness of a scope (with NULLs
/// counting as equal), the purge queries and the effect of reprocessing and completion on the purge date.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RetentionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static Document DocumentWith(RetentionPolicy? policy, Guid? productServiceId = null, DateTimeOffset? uploadedAt = null)
    {
        var id = Guid.CreateVersion7(uploadedAt ?? Now);
        var document = Document.Accept(
            id,
            $"DOC-20260925-{Random.Shared.Next(1, 999_999):D6}",
            "doc.png",
            $"documents/2026/09/25/{id:D}/original.png",
            "image/png",
            1024,
            new string('d', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt ?? Now,
            productServiceId,
            policy);
        document.MarkQueued(uploadedAt ?? Now);

        return document;
    }

    private async Task<RetentionPolicy> GlobalAsync()
    {
        await using var context = fixture.CreateContext();

        return await new RetentionPolicyRepository(context).FindByIdAsync(RetentionPolicy.GlobalPolicyId, Ct)
            ?? throw new InvalidOperationException("The migration did not seed the global policy.");
    }

    // ---- migration -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_migration_semeia_exatamente_uma_politica_global_de_365_dias()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();
        var globals = await context.RetentionPolicies.AsNoTracking()
            .Where(policy => policy.DocumentType == null && policy.ProductServiceId == null)
            .ToListAsync(Ct);

        var global = Assert.Single(globals);
        Assert.Equal(RetentionPolicy.GlobalPolicyId, global.Id);
        Assert.Equal(365, global.RetentionDays);
    }

    [Fact]
    public async Task O_banco_recusa_uma_segunda_politica_global()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            new RetentionPolicyRepository(context).AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now), null, null, 10, Now), Ct));

        Assert.Equal("RETENTION_POLICY_EXISTS", error.ErrorCode);
    }

    [Fact]
    public async Task O_banco_recusa_escopo_repetido_mesmo_com_um_dos_eixos_nulo()
    {
        RequireDatabase(fixture);

        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            var repository = new RetentionPolicyRepository(write);
            await repository.AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 10, Now), Ct);
            await repository.AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now.AddSeconds(1)), null, product.Id, 20, Now), Ct);
            await repository.AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now.AddSeconds(2)), "BR_CNH", product.Id, 30, Now), Ct);
        }

        await using var context = fixture.CreateContext();
        var duplicates = new[]
        {
            RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 99, Now),
            RetentionPolicy.Create(Guid.CreateVersion7(Now), null, product.Id, 99, Now),
            RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", product.Id, 99, Now)
        };

        foreach (var duplicate in duplicates)
        {
            await using var attempt = fixture.CreateContext();
            await Assert.ThrowsAsync<ResourceConflictException>(() => new RetentionPolicyRepository(attempt).AddAsync(duplicate, Ct));
        }

        Assert.Equal(4, await context.RetentionPolicies.CountAsync(Ct));
    }

    [Fact]
    public async Task O_banco_recusa_dias_fora_do_intervalo()
    {
        RequireDatabase(fixture);

        await using var context = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync("UPDATE retention_policies SET retention_days = 0;", Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Lista_traz_a_global_primeiro_e_filtra_por_tipo_e_produto()
    {
        RequireDatabase(fixture);

        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            var repository = new RetentionPolicyRepository(write);
            await repository.AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 10, Now), Ct);
            await repository.AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now.AddSeconds(1)), "BR_CNH", product.Id, 30, Now), Ct);
        }

        await using var read = fixture.CreateContext();
        var query = new RetentionPolicyRepository(read);

        var all = await query.ListAsync(new RetentionPolicyFilter(null, null, 1, 10), Ct);
        Assert.Equal(
            [RetentionScope.Global, RetentionScope.DocumentType, RetentionScope.DocumentTypeAndProductService],
            all.Items.Select(policy => policy.Scope));
        Assert.Equal("CONTA-PJ", all.Items[2].ProductService!.Code);

        Assert.Equal(2, (await query.ListAsync(new RetentionPolicyFilter("br_cnh", null, 1, 10), Ct)).TotalCount);
        Assert.Single((await query.ListAsync(new RetentionPolicyFilter(null, product.Id, 1, 10), Ct)).Items);
    }

    // ---- documento e política -----------------------------------------------------------------------------

    [Fact]
    public async Task Documento_grava_e_devolve_a_data_de_expurgo_e_a_politica()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = DocumentWith(global);

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: false, Ct);

        Assert.Equal(Now.AddDays(365), loaded!.ExpiresAt);
        Assert.Equal(365, loaded.RetentionDays);
        Assert.True(loaded.RetentionPolicy!.IsGlobal);
    }

    [Fact]
    public async Task Excluir_a_politica_solta_o_documento_mas_ele_mantem_data_e_dias()
    {
        RequireDatabase(fixture);

        var policy = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 30, Now);
        var document = DocumentWith(policy);

        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(policy, Ct);
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        await using (var delete = fixture.CreateContext())
        {
            var repository = new RetentionPolicyRepository(delete);
            await repository.RemoveAsync((await repository.FindByIdAsync(policy.Id, Ct))!, Ct);
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: false, Ct);

        Assert.Null(loaded!.RetentionPolicyId);
        Assert.Null(loaded.RetentionPolicy);
        Assert.Equal(Now.AddDays(30), loaded.ExpiresAt);
        Assert.Equal(30, loaded.RetentionDays);
    }

    [Fact]
    public async Task Produto_com_politica_nao_pode_ser_excluido()
    {
        RequireDatabase(fixture);

        var product = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", "Conta PJ", true, Now);
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            await new RetentionPolicyRepository(write).AddAsync(RetentionPolicy.Create(Guid.CreateVersion7(Now), null, product.Id, 30, Now), Ct);
        }

        await using var context = fixture.CreateContext();
        var repository = new ProductServiceRepository(context);

        Assert.True(await repository.IsReferencedAsync(product.Id, Ct));
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RemoveAsync((await repository.FindByIdAsync(product.Id, Ct))!, Ct));
    }

    // ---- reprocessamento e conclusão ----------------------------------------------------------------------

    [Fact]
    public async Task Reprocessar_recomeca_a_contagem_pela_politica_recebida()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = DocumentWith(global);
        document.MarkCompleted(Now);
        var shorter = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 10, Now);

        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(shorter, Ct);
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        var later = Now.AddDays(100);
        await using (var context = fixture.CreateContext())
        {
            Assert.Equal(ReprocessOutcome.Queued, await new DocumentRepository(context).QueueReprocessingAsync(document.Id, later, shorter, Ct));
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: false, Ct);

        Assert.Equal(later.AddDays(10), loaded!.ExpiresAt);
        Assert.Equal(shorter.Id, loaded.RetentionPolicyId);
    }

    [Fact]
    public async Task Reprocessar_documento_expurgado_devolve_purged_e_nao_enfileira()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = DocumentWith(global);
        document.MarkCompleted(Now);
        document.MarkPurged(Now);

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        await using var context = fixture.CreateContext();

        Assert.Equal(ReprocessOutcome.Purged, await new DocumentRepository(context).QueueReprocessingAsync(document.Id, Now.AddDays(1), global, Ct));
        Assert.Empty(await context.ProcessingJobs.Where(job => job.DocumentId == document.Id).ToListAsync(Ct));
    }

    // ---- expurgo ------------------------------------------------------------------------------------------------

    private async Task<Document> StoreAsync(RetentionPolicy policy, DocumentStatus status, DateTimeOffset uploadedAt)
    {
        var document = DocumentWith(policy, uploadedAt: uploadedAt);

        switch (status)
        {
            case DocumentStatus.Completed: document.MarkCompleted(uploadedAt); break;
            case DocumentStatus.Failed: document.MarkFailed("OCR_UNAVAILABLE", "down", uploadedAt); break;
            case DocumentStatus.Purged: document.MarkCompleted(uploadedAt); document.MarkPurged(uploadedAt); break;
        }

        await using var write = fixture.CreateContext();
        write.Documents.Add(document);
        await write.SaveChangesAsync(Ct);

        return document;
    }

    [Fact]
    public async Task So_documento_vencido_em_estado_final_e_candidato_ao_expurgo()
    {
        RequireDatabase(fixture);

        var policy = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 10, Now);
        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(policy, Ct);
        }

        var expiredCompleted = await StoreAsync(policy, DocumentStatus.Completed, Now);
        var expiredFailed = await StoreAsync(policy, DocumentStatus.Failed, Now.AddHours(1));
        var expiredButRunning = await StoreAsync(policy, DocumentStatus.Queued, Now.AddHours(2));
        var notYet = await StoreAsync(policy, DocumentStatus.Completed, Now.AddDays(5));
        var alreadyPurged = await StoreAsync(policy, DocumentStatus.Purged, Now.AddHours(3));

        await using var context = fixture.CreateContext();
        var repository = new DocumentRepository(context);
        var found = await repository.FindPurgeableAsync(Now.AddDays(11), 100, [], Ct);

        Assert.Equal([expiredCompleted.Id, expiredFailed.Id], found.Select(candidate => candidate.DocumentId));
        Assert.DoesNotContain(found, candidate => candidate.DocumentId == expiredButRunning.Id);
        Assert.DoesNotContain(found, candidate => candidate.DocumentId == notYet.Id);
        Assert.DoesNotContain(found, candidate => candidate.DocumentId == alreadyPurged.Id);
    }

    [Fact]
    public async Task O_lote_respeita_o_limite_e_a_exclusao()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var first = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-400));
        var second = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-399));
        await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-398));

        await using var context = fixture.CreateContext();
        var repository = new DocumentRepository(context);

        Assert.Equal([first.Id, second.Id], (await repository.FindPurgeableAsync(Now, 2, [], Ct)).Select(candidate => candidate.DocumentId));
        Assert.DoesNotContain(
            await repository.FindPurgeableAsync(Now, 10, [first.Id], Ct),
            candidate => candidate.DocumentId == first.Id);
    }

    [Fact]
    public async Task Marcar_expurgado_grava_status_evento_e_data_e_so_uma_vez()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-400));

        await using (var first = fixture.CreateContext())
        {
            Assert.True(await new DocumentRepository(first).MarkPurgedAsync(document.Id, Now, Ct));
        }

        await using (var second = fixture.CreateContext())
        {
            Assert.False(await new DocumentRepository(second).MarkPurgedAsync(document.Id, Now.AddHours(1), Ct));
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: true, Ct);

        Assert.Equal(DocumentStatus.Purged, loaded!.Status);
        Assert.Equal(Now, loaded.PurgedAt);
        Assert.Single(loaded.Events, entry => entry.EventType == DocumentEventTypes.Purged);
    }

    [Fact]
    public async Task Marcar_expurgado_um_documento_que_deixou_de_ser_elegivel_nao_faz_nada()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-400));

        // A reprocess got there first: the document is queued again and has a new deadline.
        await using (var reprocess = fixture.CreateContext())
        {
            await new DocumentRepository(reprocess).QueueReprocessingAsync(document.Id, Now, global, Ct);
        }

        await using var context = fixture.CreateContext();

        Assert.False(await new DocumentRepository(context).MarkPurgedAsync(document.Id, Now, Ct));
        Assert.Equal(DocumentStatus.Queued, (await context.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id, Ct)).Status);
    }

    [Fact]
    public async Task Duas_marcacoes_simultaneas_do_mesmo_documento_resultam_em_um_unico_evento()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var document = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-400));

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();
        var results = await Task.WhenAll(
            new DocumentRepository(contextA).MarkPurgedAsync(document.Id, Now, Ct),
            new DocumentRepository(contextB).MarkPurgedAsync(document.Id, Now, Ct));

        Assert.Single(results, won => won);

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: true, Ct);
        Assert.Single(loaded!.Events, entry => entry.EventType == DocumentEventTypes.Purged);
    }

    private async Task AddExtractionsAsync(Document document, int count = 1)
    {
        var job = ProcessingJob.CreateForDocument(document.Id, Now);
        var extractions = Enumerable.Range(0, count).Select(_ => DocumentExtraction.Create(
            document.Id, job.Id, "paddleocr", "PP-OCRv5 test", "rules-2.0.0", "br-cnh-1.1.0", 1,
            "CPF 111.444.777-35",
            "[{\"pageNumber\":1,\"text\":\"CPF 111.444.777-35\"}]",
            "{\"pages\":[{\"page\":1,\"raw\":{}}]}",
            "{\"cpf\":\"11144477735\"}",
            0.99m,
            Now,
            [
                ExtractedField.Create("cpf", "111.444.777-35", "11144477735", 1.0m, 1, "[]", "VALID", "[]"),
                ExtractedField.Create("name", null, null, null, null, "[]", "NOT_FOUND", "[]")
            ])).ToList();

        await using var write = fixture.CreateContext();
        write.ProcessingJobs.Add(job);
        write.Extractions.AddRange(extractions);
        await write.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Expurgar_apaga_extracoes_e_campos_mas_mantem_o_documento_o_historico_e_os_outros_documentos()
    {
        RequireDatabase(fixture);

        var global = await GlobalAsync();
        var expired = await StoreAsync(global, DocumentStatus.Completed, Now.AddDays(-400));
        var untouched = await StoreAsync(global, DocumentStatus.Completed, Now);
        await AddExtractionsAsync(expired, count: 2);
        await AddExtractionsAsync(untouched);

        await using (var context = fixture.CreateContext())
        {
            Assert.True(await new DocumentRepository(context).MarkPurgedAsync(expired.Id, Now, Ct));
        }

        await using var read = fixture.CreateContext();
        Assert.Equal(0, await read.Extractions.CountAsync(item => item.DocumentId == expired.Id, Ct));
        Assert.Equal(0, await read.ExtractedFields.CountAsync(field => read.Extractions.All(item => item.Id != field.ExtractionId), Ct));
        Assert.Equal(1, await read.Extractions.CountAsync(item => item.DocumentId == untouched.Id, Ct));
        Assert.Equal(2, await read.ExtractedFields.CountAsync(Ct));

        var loaded = await new DocumentRepository(read).FindByIdAsync(expired.Id, includeEvents: true, Ct);
        Assert.Equal(DocumentStatus.Purged, loaded!.Status);
        Assert.Equal(expired.Protocol, loaded.Protocol);
        Assert.Equal(expired.UploadedAt, loaded.UploadedAt);
        Assert.Equal(expired.OriginalFileName, loaded.OriginalFileName);
        Assert.Equal(1, await read.ProcessingJobs.CountAsync(job => job.DocumentId == expired.Id, Ct));

        var purged = Assert.Single(loaded.Events, entry => entry.EventType == DocumentEventTypes.Purged);
        Assert.Equal("reason=RETENTION_EXPIRED deleted=file,ocr_text,extracted_fields", purged.Details);
        Assert.Contains(loaded.Events, entry => entry.EventType != DocumentEventTypes.Purged);
    }

    [Fact]
    public async Task Expurgar_documento_sem_extracao_registra_so_o_arquivo()
    {
        RequireDatabase(fixture);

        var document = await StoreAsync(await GlobalAsync(), DocumentStatus.Failed, Now.AddDays(-400));

        await using (var context = fixture.CreateContext())
        {
            Assert.True(await new DocumentRepository(context).MarkPurgedAsync(document.Id, Now, Ct));
        }

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: true, Ct);

        Assert.Equal(
            "reason=RETENTION_EXPIRED deleted=file",
            Assert.Single(loaded!.Events, entry => entry.EventType == DocumentEventTypes.Purged).Details);
    }
}
