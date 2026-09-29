using DocReader.Application.Retention;
using DocReader.Domain.Documents;
using DocReader.Domain.Retention;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Recomputing expiresAt against a real PostgreSQL, after a retention policy's duration changes: enqueueing a
/// reapply, the worker-facing claim, and the batch itself running to completion over more documents than fit in
/// one batch.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RetentionReapplyPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static Document DocumentWith(RetentionPolicy policy, DateTimeOffset uploadedAt)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            id,
            $"DOC-20260928-{Random.Shared.Next(1, 999_999):D6}",
            "doc.png",
            $"documents/2026/09/28/{id:D}/original.png",
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
        document.MarkCompleted(uploadedAt);

        return document;
    }

    private RetentionReapplyService ServiceOf(DocReaderDbContext context) => new(
        new RetentionReapplyRequestRepository(context),
        new RetentionPolicyRepository(context),
        new FakeTimeProvider(Now),
        NullLogger<RetentionReapplyService>.Instance);

    [Fact]
    public async Task Reaplicar_recalcula_expiresAt_de_todos_os_documentos_da_politica_e_registra_o_evento()
    {
        RequireDatabase(fixture);

        var policy = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CNH", null, 365, Now);
        var documents = Enumerable.Range(0, 10)
            .Select(index => DocumentWith(policy, Now.AddMinutes(-index)))
            .ToList();

        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(policy, Ct);
            write.Documents.AddRange(documents);
            await write.SaveChangesAsync(Ct);
        }

        await using (var change = fixture.CreateContext())
        {
            var tracked = await new RetentionPolicyRepository(change).FindByIdAsync(policy.Id, Ct);
            tracked!.ChangeRetention(30, Now);
            await change.SaveChangesAsync(Ct);
        }

        RetentionReapplyRequest request;
        await using (var write = fixture.CreateContext())
        {
            request = await ServiceOf(write).EnqueueAsync(policy.Id, Ct);
        }

        await using (var process = fixture.CreateContext())
        {
            await ServiceOf(process).ProcessAsync(request.Id, Ct);
        }

        await using (var read = fixture.CreateContext())
        {
            var completed = await new RetentionReapplyRequestRepository(read).FindByIdAsync(request.Id, Ct);
            Assert.Equal(RetentionReapplyStatus.Completed, completed!.Status);
            Assert.Equal(10, completed.DocumentsUpdated);
        }

        foreach (var seed in documents)
        {
            await using var read = fixture.CreateContext();
            var loaded = await read.Documents
                .Include(document => document.Events)
                .AsNoTracking()
                .FirstAsync(document => document.Id == seed.Id, Ct);

            Assert.Equal(loaded.UploadedAt.AddDays(30), loaded.ExpiresAt);
            Assert.Equal(30, loaded.RetentionDays);
            Assert.Equal(policy.Id, loaded.RetentionPolicyId);
            Assert.Contains(loaded.Events, entry => entry.EventType == DocumentEventTypes.RetentionPolicyReapplied);
        }
    }

    [Fact]
    public async Task Reaplicar_percorre_mais_de_um_lote()
    {
        RequireDatabase(fixture);

        var policy = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CIN", null, 365, Now);
        var documents = Enumerable.Range(0, RetentionReapplyService.BatchSize + 5)
            .Select(index => DocumentWith(policy, Now.AddSeconds(-index)))
            .ToList();

        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(policy, Ct);
            write.Documents.AddRange(documents);
            await write.SaveChangesAsync(Ct);
        }

        await using (var change = fixture.CreateContext())
        {
            var tracked = await new RetentionPolicyRepository(change).FindByIdAsync(policy.Id, Ct);
            tracked!.ChangeRetention(90, Now);
            await change.SaveChangesAsync(Ct);
        }

        RetentionReapplyRequest request;
        await using (var write = fixture.CreateContext())
        {
            request = await ServiceOf(write).EnqueueAsync(policy.Id, Ct);
        }

        await using (var process = fixture.CreateContext())
        {
            await ServiceOf(process).ProcessAsync(request.Id, Ct);
        }

        await using var read = fixture.CreateContext();
        var completed = await new RetentionReapplyRequestRepository(read).FindByIdAsync(request.Id, Ct);
        Assert.Equal(RetentionReapplyStatus.Completed, completed!.Status);
        Assert.Equal(documents.Count, completed.DocumentsUpdated);

        var stillAt365 = await read.Documents.CountAsync(document => document.RetentionPolicyId == policy.Id && document.RetentionDays == 365, Ct);
        Assert.Equal(0, stillAt365);
    }

    [Fact]
    public async Task Reivindicar_o_proximo_pendente_e_atomico_e_devolve_null_quando_nao_ha_nada()
    {
        RequireDatabase(fixture);

        await using (var empty = fixture.CreateContext())
        {
            Assert.Null(await new RetentionReapplyRequestRepository(empty).ClaimNextPendingAsync(Now, Ct));
        }

        var policy = RetentionPolicy.Create(Guid.CreateVersion7(Now), "BR_CCMEI", null, 200, Now);

        RetentionReapplyRequest request;
        await using (var write = fixture.CreateContext())
        {
            await new RetentionPolicyRepository(write).AddAsync(policy, Ct);
            request = await ServiceOf(write).EnqueueAsync(policy.Id, Ct);
        }

        await using (var claimContext = fixture.CreateContext())
        {
            var claimed = await new RetentionReapplyRequestRepository(claimContext).ClaimNextPendingAsync(Now.AddMinutes(1), Ct);
            Assert.NotNull(claimed);
            Assert.Equal(request.Id, claimed!.Id);
            Assert.Equal(RetentionReapplyStatus.Running, claimed.Status);
        }

        await using var read = fixture.CreateContext();
        Assert.Null(await new RetentionReapplyRequestRepository(read).ClaimNextPendingAsync(Now.AddMinutes(2), Ct));
    }
}
