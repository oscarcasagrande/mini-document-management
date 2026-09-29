using DocReader.Application.Errors;
using DocReader.Application.Retention;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Recomputing expiresAt when a retention policy's duration changes: enqueue, and the batch run.</summary>
public sealed class RetentionReapplyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryRetentionPolicyStore _policies = new();
    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly FakeTimeProvider _clock = new(Now);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InMemoryRetentionReapplyRequestStore Requests() => new(_documents);

    private RetentionReapplyService Service(InMemoryRetentionReapplyRequestStore requests) =>
        new(requests, _policies, _clock, NullLogger<RetentionReapplyService>.Instance);

    private async Task<Document> DocumentWithAsync(RetentionPolicy policy)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            $"DOC-20260925-{Random.Shared.Next(1, 999_999):D6}",
            "doc.png",
            $"documents/{id:D}/original.png",
            "image/png",
            1024,
            new string('d', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            Now,
            null,
            policy);
        document.MarkCompleted(Now);
        await _documents.AcceptAsync(document, ProcessingJob.CreateForDocument(id, Now), null, Ct);

        return document;
    }

    [Fact]
    public async Task Enfileirar_cria_um_pedido_pendente()
    {
        var policy = await new RetentionPolicyService(
                _policies, new InMemoryProductServiceStore(), InMemoryDocumentTypeStore.WithBuiltIns(), _clock, NullLogger<RetentionPolicyService>.Instance)
            .CreateAsync("BR_CNH", null, 365, Ct);

        var request = await Service(Requests()).EnqueueAsync(policy.Id, Ct);

        Assert.Equal(RetentionReapplyStatus.Pending, request.Status);
        Assert.Equal(policy.Id, request.RetentionPolicyId);
        Assert.Equal(Now, request.RequestedAt);
    }

    [Fact]
    public async Task Enfileirar_para_politica_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(Requests()).EnqueueAsync(Guid.NewGuid(), Ct));

        Assert.Equal("retention-policy", error.Resource);
    }

    [Fact]
    public async Task Processar_move_pendente_para_executando_e_concluido_e_conta_os_documentos()
    {
        var requests = Requests();
        var global = _policies.Global;

        for (var index = 0; index < 3; index++)
        {
            await DocumentWithAsync(global);
        }

        global.ChangeRetention(30, Now);
        var request = await Service(requests).EnqueueAsync(global.Id, Ct);
        Assert.Equal(RetentionReapplyStatus.Pending, request.Status);

        await Service(requests).ProcessAsync(request.Id, Ct);

        Assert.Equal(RetentionReapplyStatus.Completed, request.Status);
        Assert.Equal(3, request.DocumentsUpdated);
        Assert.NotNull(request.StartedAt);
        Assert.NotNull(request.CompletedAt);
        Assert.All(_documents.Documents, document => Assert.Equal(Now.AddDays(30), document.ExpiresAt));
        Assert.All(_documents.Documents, document =>
            Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.RetentionPolicyReapplied));
    }

    [Fact]
    public async Task Processar_documento_ja_no_valor_novo_nao_e_tocado_de_novo()
    {
        var requests = Requests();
        var global = _policies.Global;
        var document = await DocumentWithAsync(global);

        global.ChangeRetention(30, Now);
        var request = await Service(requests).EnqueueAsync(global.Id, Ct);
        await Service(requests).ProcessAsync(request.Id, Ct);
        var eventsAfterFirstRun = document.Events.Count;

        var secondRequest = await Service(requests).EnqueueAsync(global.Id, Ct);
        await Service(requests).ProcessAsync(secondRequest.Id, Ct);

        Assert.Equal(0, secondRequest.DocumentsUpdated);
        Assert.Equal(eventsAfterFirstRun, document.Events.Count);
    }
}
