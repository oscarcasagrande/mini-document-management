using DocReader.Application.Audit;
using DocReader.Domain.Audit;
using DocReader.Infrastructure.Persistence;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// The audit trail against a real PostgreSQL: <see cref="EfAuditLogStore"/> writes and, for
/// <c>GET /api/v1/audit-logs</c>, filters by userId, action and date range, newest first.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AuditLogPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static AuditLog Entry(string? userId, string action, DateTimeOffset occurredAt) =>
        AuditLog.Create(Guid.CreateVersion7(occurredAt), userId, action, "Document", Guid.NewGuid().ToString(), occurredAt, "192.0.2.1", "xunit", null);

    [Fact]
    public async Task ListAsync_filtra_por_userId_action_e_intervalo_de_datas_e_ordena_do_mais_novo()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using (var write = fixture.CreateContext())
        {
            var store = new EfAuditLogStore(write);
            await store.AddAsync(Entry("alice", "DOCUMENT_UPLOADED", Now), Ct);
            await store.AddAsync(Entry("alice", "DOCUMENT_DELETED", Now.AddMinutes(1)), Ct);
            await store.AddAsync(Entry("bob", "DOCUMENT_UPLOADED", Now.AddMinutes(2)), Ct);
        }

        await using var read = fixture.CreateContext();
        var reader = new EfAuditLogStore(read);

        var byUser = await reader.ListAsync(new AuditLogFilter("alice", null, null, null, 1, 20), Ct);
        Assert.Equal(2, byUser.TotalCount);
        Assert.All(byUser.Items, entry => Assert.Equal("alice", entry.UserId));
        // Newest first.
        Assert.Equal("DOCUMENT_DELETED", byUser.Items[0].Action);

        var byAction = await reader.ListAsync(new AuditLogFilter(null, "DOCUMENT_UPLOADED", null, null, 1, 20), Ct);
        Assert.Equal(2, byAction.TotalCount);
        Assert.All(byAction.Items, entry => Assert.Equal("DOCUMENT_UPLOADED", entry.Action));

        var byRange = await reader.ListAsync(new AuditLogFilter(null, null, Now, Now.AddMinutes(1), 1, 20), Ct);
        Assert.Equal(2, byRange.TotalCount);
        Assert.DoesNotContain(byRange.Items, entry => entry.UserId == "bob");
    }
}
