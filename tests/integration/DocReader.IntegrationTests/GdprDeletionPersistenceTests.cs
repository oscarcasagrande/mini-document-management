using DocReader.Domain.Documents;
using DocReader.Domain.GdprDeletion;
using DocReader.Domain.Retention;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// The GDPR/LGPD deletion workflow against a real PostgreSQL: creating a request appends the timeline event in
/// the same transaction, approving and executing use the same lock-and-recheck idiom as
/// <c>RetentionReapplyRequestRepository</c>, and the worker's claim-execute path (<see cref="GdprDeletionRequestRepository.MarkExecutedAsync"/>)
/// really is atomic under concurrency, not just in the in-memory fake.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class GdprDeletionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static Document CompletedDocument(DateTimeOffset uploadedAt, int retentionDays = 1)
    {
        var policy = RetentionPolicy.Create(RetentionPolicy.GlobalPolicyId, null, null, retentionDays, uploadedAt);
        var id = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            id,
            $"DOC-20260929-{Random.Shared.Next(1, 999_999):D6}",
            "doc.png",
            $"documents/2026/09/29/{id:D}/original.png",
            "image/png",
            1024,
            new string('d', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt,
            null,
            policy);
        document.RecordClassification("BR_CNH", 0.9m, uploadedAt);
        document.MarkCompleted(uploadedAt);

        return document;
    }

    [Fact]
    public async Task Pedido_aprovacao_e_execucao_apagam_o_conteudo_e_marcam_tudo_atomicamente()
    {
        RequireDatabase(fixture);

        var document = CompletedDocument(Now.AddDays(-10), retentionDays: 1);

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);
        }

        GdprDeletionRequest request;
        await using (var write = fixture.CreateContext())
        {
            var repository = new GdprDeletionRequestRepository(write);
            request = GdprDeletionRequest.Create(Guid.CreateVersion7(Now), document.Id, "alice", "titular pediu", Now);
            await repository.AddAsync(request, Ct);
        }

        await using (var read = fixture.CreateContext())
        {
            var loaded = await read.Documents.Include(d => d.Events).AsNoTracking().FirstAsync(d => d.Id == document.Id, Ct);
            Assert.Contains(loaded.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionRequested);
        }

        await using (var write = fixture.CreateContext())
        {
            var approved = await new GdprDeletionRequestRepository(write).ApproveAsync(request.Id, "bob", Now, Ct);
            Assert.NotNull(approved);
            Assert.Equal(GdprDeletionRequestStatus.Approved, approved!.Status);
        }

        await using (var read = fixture.CreateContext())
        {
            var candidate = await new GdprDeletionRequestRepository(read).FindNextApprovedAsync(Ct);
            Assert.NotNull(candidate);
            Assert.Equal(request.Id, candidate!.RequestId);
            Assert.Equal(document.Id, candidate.DocumentId);
        }

        await using (var write = fixture.CreateContext())
        {
            var result = await new GdprDeletionRequestRepository(write).MarkExecutedAsync(request.Id, document.Id, Now, Ct);
            Assert.NotNull(result);
            Assert.Equal("bob", result!.ApprovedBy);
        }

        await using (var read = fixture.CreateContext())
        {
            var loadedDocument = await read.Documents.Include(d => d.Events).AsNoTracking().FirstAsync(d => d.Id == document.Id, Ct);
            Assert.Equal(DocumentStatus.Purged, loadedDocument.Status);
            var executedEvent = Assert.Single(loadedDocument.Events, entry => entry.EventType == DocumentEventTypes.GdprDeletionExecuted);
            Assert.Contains($"requestId={request.Id}", executedEvent.Details, StringComparison.Ordinal);
            Assert.Contains("deleted=file", executedEvent.Details, StringComparison.Ordinal);

            var extractionCount = await read.Extractions.CountAsync(extraction => extraction.DocumentId == document.Id, Ct);
            Assert.Equal(0, extractionCount);

            var loadedRequest = await new GdprDeletionRequestRepository(read).FindByIdAsync(request.Id, Ct);
            Assert.Equal(GdprDeletionRequestStatus.Executed, loadedRequest!.Status);
            Assert.NotNull(loadedRequest.ExecutedAt);
        }

        // No candidate is left: FindNextApprovedAsync only sees APPROVED requests.
        await using var final = fixture.CreateContext();
        Assert.Null(await new GdprDeletionRequestRepository(final).FindNextApprovedAsync(Ct));
    }

    [Fact]
    public async Task Executar_duas_vezes_e_idempotente_a_segunda_perde_a_corrida()
    {
        RequireDatabase(fixture);

        var document = CompletedDocument(Now.AddDays(-10), retentionDays: 1);
        GdprDeletionRequest request;

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);

            var repository = new GdprDeletionRequestRepository(write);
            request = GdprDeletionRequest.Create(Guid.CreateVersion7(Now), document.Id, null, null, Now);
            await repository.AddAsync(request, Ct);
        }

        await using (var write = fixture.CreateContext())
        {
            await new GdprDeletionRequestRepository(write).ApproveAsync(request.Id, "bob", Now, Ct);
        }

        await using (var first = fixture.CreateContext())
        {
            var firstResult = await new GdprDeletionRequestRepository(first).MarkExecutedAsync(request.Id, document.Id, Now, Ct);
            Assert.NotNull(firstResult);
        }

        await using var second = fixture.CreateContext();
        var secondResult = await new GdprDeletionRequestRepository(second).MarkExecutedAsync(request.Id, document.Id, Now, Ct);
        Assert.Null(secondResult);
    }

    [Fact]
    public async Task Aprovar_duas_vezes_em_paralelo_so_uma_vence()
    {
        RequireDatabase(fixture);

        var document = CompletedDocument(Now.AddDays(-10), retentionDays: 1);
        GdprDeletionRequest request;

        await using (var write = fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync(Ct);

            var repository = new GdprDeletionRequestRepository(write);
            request = GdprDeletionRequest.Create(Guid.CreateVersion7(Now), document.Id, null, null, Now);
            await repository.AddAsync(request, Ct);
        }

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();

        var taskA = new GdprDeletionRequestRepository(contextA).ApproveAsync(request.Id, "operator-a", Now, Ct);
        var taskB = new GdprDeletionRequestRepository(contextB).ApproveAsync(request.Id, "operator-b", Now, Ct);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, result => result is not null);

        await using var read = fixture.CreateContext();
        var loaded = await new GdprDeletionRequestRepository(read).FindByIdAsync(request.Id, Ct);
        Assert.Equal(GdprDeletionRequestStatus.Approved, loaded!.Status);
        Assert.True(loaded.ApprovedBy is "operator-a" or "operator-b");
    }
}
