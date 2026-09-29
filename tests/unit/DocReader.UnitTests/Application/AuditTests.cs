using DocReader.Application.Audit;
using DocReader.Domain.Audit;
using DocReader.UnitTests.Fakes;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// The generic audit trail: the pure decision logic behind the API's <c>AuditedAttribute</c> filter
/// (<see cref="AuditDecision"/>, exercised here without any ASP.NET Core type) and the filtered listing behind
/// <c>GET /api/v1/audit-logs</c> (<see cref="AuditLogService.ListAsync"/>).
/// </summary>
public sealed class AuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- AuditDecision.IsSuccessStatusCode ------------------------------------------------------------------

    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(202, true)]
    [InlineData(204, true)]
    [InlineData(299, true)]
    [InlineData(199, false)]
    [InlineData(300, false)]
    [InlineData(400, false)]
    [InlineData(500, false)]
    [InlineData(null, false)]
    public void IsSuccessStatusCode_so_da_2xx(int? statusCode, bool expected)
    {
        Assert.Equal(expected, AuditDecision.IsSuccessStatusCode(statusCode));
    }

    // ---- AuditDecision.ResourceId / ResourceIdFromRoute -----------------------------------------------------

    [Fact]
    public void ResourceIdFromRoute_prefere_id_a_jobId()
    {
        var routeValues = new Dictionary<string, object?> { ["id"] = Guid.Parse("11111111-1111-1111-1111-111111111111"), ["jobId"] = Guid.Parse("22222222-2222-2222-2222-222222222222") };

        var resourceId = AuditDecision.ResourceIdFromRoute(routeValues, ["id", "jobId"]);

        Assert.Equal("11111111-1111-1111-1111-111111111111", resourceId);
    }

    [Fact]
    public void ResourceIdFromRoute_cai_para_jobId_quando_id_ausente()
    {
        var routeValues = new Dictionary<string, object?> { ["jobId"] = Guid.Parse("22222222-2222-2222-2222-222222222222") };

        var resourceId = AuditDecision.ResourceIdFromRoute(routeValues, ["id", "jobId"]);

        Assert.Equal("22222222-2222-2222-2222-222222222222", resourceId);
    }

    [Fact]
    public void ResourceIdFromRoute_devolve_nulo_sem_nenhuma_chave()
    {
        var resourceId = AuditDecision.ResourceIdFromRoute(new Dictionary<string, object?>(), ["id", "jobId"]);

        Assert.Null(resourceId);
    }

    [Fact]
    public void ResourceId_prefere_a_rota_ao_corpo_da_resposta()
    {
        var routeValues = new Dictionary<string, object?> { ["id"] = Guid.Parse("11111111-1111-1111-1111-111111111111") };
        var body = new IdBody(Guid.Parse("99999999-9999-9999-9999-999999999999"));

        var resourceId = AuditDecision.ResourceId(routeValues, body);

        Assert.Equal("11111111-1111-1111-1111-111111111111", resourceId);
    }

    [Fact]
    public void ResourceId_cai_para_o_id_do_corpo_quando_a_rota_nao_tem_um()
    {
        var body = new IdBody(Guid.Parse("99999999-9999-9999-9999-999999999999"));

        var resourceId = AuditDecision.ResourceId(new Dictionary<string, object?>(), body);

        Assert.Equal("99999999-9999-9999-9999-999999999999", resourceId);
    }

    [Fact]
    public void ResourceId_devolve_nulo_sem_rota_e_sem_corpo()
    {
        Assert.Null(AuditDecision.ResourceId(new Dictionary<string, object?>(), null));
    }

    // ---- AuditDecision.IdOf ----------------------------------------------------------------------------------

    [Fact]
    public void IdOf_le_propriedade_Id_guid_por_reflexao()
    {
        var body = new IdBody(Guid.Parse("99999999-9999-9999-9999-999999999999"));

        Assert.Equal("99999999-9999-9999-9999-999999999999", AuditDecision.IdOf(body));
    }

    [Fact]
    public void IdOf_le_propriedade_Id_string()
    {
        Assert.Equal("abc-123", AuditDecision.IdOf(new StringIdBody("abc-123")));
    }

    [Fact]
    public void IdOf_devolve_nulo_sem_propriedade_Id()
    {
        Assert.Null(AuditDecision.IdOf(new NoIdBody("x")));
    }

    [Fact]
    public void IdOf_devolve_nulo_para_valor_nulo()
    {
        Assert.Null(AuditDecision.IdOf(null));
    }

    // ---- AuditDecision.FindRequestBody / TouchedFields ------------------------------------------------------

    [Fact]
    public void FindRequestBody_ignora_guid_string_e_cancellation_token_e_acha_a_classe()
    {
        object?[] arguments = [Guid.NewGuid(), "channel-header", Ct, new UpdateBody("Nova Corp", true, null)];

        var body = AuditDecision.FindRequestBody(arguments);

        Assert.IsType<UpdateBody>(body);
    }

    [Fact]
    public void FindRequestBody_devolve_nulo_so_com_primitivos()
    {
        object?[] arguments = [Guid.NewGuid(), Ct];

        Assert.Null(AuditDecision.FindRequestBody(arguments));
    }

    [Fact]
    public void TouchedFields_lista_nomes_de_propriedade_preenchidas_sem_valores()
    {
        var changes = AuditDecision.TouchedFields(new UpdateBody("Nova Corp", true, null)) ?? string.Empty;

        Assert.Contains("Name", changes);
        Assert.Contains("Active", changes);
        Assert.DoesNotContain("Secret", changes);
        // Never the value: the field name may appear, but not the string "Nova Corp".
        Assert.DoesNotContain("Nova Corp", changes, StringComparison.Ordinal);
    }

    [Fact]
    public void TouchedFields_exclui_a_propriedade_Id()
    {
        var changes = AuditDecision.TouchedFields(new IdBody(Guid.NewGuid()));

        Assert.Null(changes);
    }

    [Fact]
    public void TouchedFields_devolve_nulo_sem_corpo()
    {
        Assert.Null(AuditDecision.TouchedFields(null));
    }

    // ---- AuditLogService.ListAsync / filtros ------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_filtra_por_userId_exato()
    {
        var store = new InMemoryAuditLogStore();
        await Seed(store, userId: "alice", action: "DOCUMENT_UPLOADED", occurredAt: T(1));
        await Seed(store, userId: "bob", action: "DOCUMENT_UPLOADED", occurredAt: T(2));

        var page = await store.ListAsync(new AuditLogFilter("alice", null, null, null, 1, 20), Ct);

        var entry = Assert.Single(page.Items);
        Assert.Equal("alice", entry.UserId);
    }

    [Fact]
    public async Task ListAsync_filtra_por_action_exato()
    {
        var store = new InMemoryAuditLogStore();
        await Seed(store, userId: null, action: "DOCUMENT_UPLOADED", occurredAt: T(1));
        await Seed(store, userId: null, action: "DOCUMENT_DELETED", occurredAt: T(2));

        var page = await store.ListAsync(new AuditLogFilter(null, "DOCUMENT_DELETED", null, null, 1, 20), Ct);

        var entry = Assert.Single(page.Items);
        Assert.Equal("DOCUMENT_DELETED", entry.Action);
    }

    [Fact]
    public async Task ListAsync_filtra_por_intervalo_de_datas_inclusive()
    {
        var store = new InMemoryAuditLogStore();
        await Seed(store, userId: null, action: "A", occurredAt: T(1));
        await Seed(store, userId: null, action: "A", occurredAt: T(2));
        await Seed(store, userId: null, action: "A", occurredAt: T(3));

        var page = await store.ListAsync(new AuditLogFilter(null, null, T(1), T(2), 1, 20), Ct);

        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, entry => Assert.True(entry.OccurredAt >= T(1) && entry.OccurredAt <= T(2)));
    }

    [Fact]
    public async Task ListAsync_ordena_do_mais_novo_para_o_mais_velho_e_pagina()
    {
        var store = new InMemoryAuditLogStore();
        await Seed(store, userId: null, action: "A", occurredAt: T(1));
        await Seed(store, userId: null, action: "A", occurredAt: T(2));
        await Seed(store, userId: null, action: "A", occurredAt: T(3));

        var page = await store.ListAsync(new AuditLogFilter(null, null, null, null, 1, 2), Ct);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(T(3), page.Items[0].OccurredAt);
        Assert.Equal(T(2), page.Items[1].OccurredAt);
    }

    private static DateTimeOffset T(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    private static Task Seed(InMemoryAuditLogStore store, string? userId, string action, DateTimeOffset occurredAt)
    {
        var entry = AuditLog.Create(Guid.NewGuid(), userId, action, "Document", Guid.NewGuid().ToString(), occurredAt, null, null, null);

        return store.AddAsync(entry, Ct);
    }

    private sealed record IdBody(Guid Id);

    private sealed record StringIdBody(string Id);

    private sealed record NoIdBody(string Name);

    private sealed record UpdateBody(string? Name, bool Active, string? Secret);
}
