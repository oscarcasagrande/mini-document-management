using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Errors;
using DocReader.Application.Extraction;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// The processing pipeline against fakes: which stages it walks, when it renews the lock, and what
/// it does on each kind of failure. The database side is covered by the integration tests.
/// </summary>
public sealed class DocumentProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>As read by PP-OCRv5 from samples/synthetic/ocr/cpf-card-limpo.png, in the order it returned them.</summary>
    private static readonly string[] CpfCardLines =
    [
        "REPUBLICA FEDERATIVA DO BRASIL",
        "MINISTERIO DA FAZENDA",
        "SECRETARIA DA RECEITA FEDERAL",
        "CADASTRO DE PESSOAS FISICAS",
        "NUMERO DE INSCRICAO",
        "111.444.777-35",
        "NOME",
        "MARIA APARECIDA DA SILVA SOUZA",
        "NASCIMENTO",
        "INSCRICAO EM 02/09/2003",
        "14/03/1985",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL"
    ];

    private sealed record Rig(
        DocumentProcessor Processor,
        FakeProcessingQueue Queue,
        FakeProcessingStore Store,
        ScriptedOcrProvider Provider,
        ProcessingJob Job);

    private static Document NewDocument(int pageCount = 1, string? expectedType = null)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            "DOC-20260924-000003",
            "cartao-cpf.png",
            $"documents/2026/09/24/{id:D}/original.png",
            "image/png",
            2048,
            new string('a', 64),
            pageCount,
            UploadChannel.Api,
            null,
            expectedType,
            Now);

        document.MarkQueued(Now);
        return document;
    }

    private static Rig BuildRig(
        ScriptedOcrProvider provider,
        Document? document = null,
        bool originalExists = true)
    {
        document ??= NewDocument();

        var queue = new FakeProcessingQueue();
        var store = new FakeProcessingStore(document);
        var time = new FakeTimeProvider(Now);

        var processor = new DocumentProcessor(
            queue,
            store,
            new StubStorage(originalExists),
            provider,
            new RulesDocumentClassifier(),
            [new BrCpfCardExtractor(time)],
            Options.Create(new ProcessingQueueOptions()),
            Options.Create(new OcrProviderOptions()),
            time,
            NullLogger<DocumentProcessor>.Instance);

        return new Rig(processor, queue, store, provider, ProcessingJob.CreateForDocument(document.Id, Now));
    }

    [Fact]
    public async Task Cartao_de_cpf_percorre_todos_os_estagios_e_completa_com_os_campos()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines));

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                DocumentStatus.Preprocessing,
                DocumentStatus.OcrRunning,
                DocumentStatus.Classifying,
                DocumentStatus.Extracting
            ],
            rig.Store.Stages.Select(stage => stage.Stage));

        Assert.Empty(rig.Queue.Failures);
        var completed = Assert.Single(rig.Store.Completed);

        Assert.Equal("BR_CPF_CARD", completed.DetectedType);
        Assert.Equal(1.0m, completed.Confidence);

        var fields = completed.Extraction.Fields.ToDictionary(field => field.FieldPath);
        Assert.Equal("11144477735", fields["cpf"].NormalizedValue);
        Assert.Equal("VALID", fields["cpf"].ValidationStatus);
        Assert.Contains("CHECK_DIGIT_VALID", fields["cpf"].ValidationMessagesJson);
        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].NormalizedValue);
        Assert.Equal("1985-03-14", fields["birthDate"].NormalizedValue);
        Assert.NotNull(completed.Extraction.OverallConfidence);
        Assert.Equal("scripted-1.0", completed.Extraction.OcrModelVersion);
        Assert.Equal(RulesDocumentClassifier.ClassifierVersion, completed.Extraction.ClassifierVersion);
        Assert.Equal(BrCpfCardExtractor.ExtractorVersion, completed.Extraction.ExtractorVersion);
    }

    [Fact]
    public async Task Renova_a_reserva_antes_de_comecar_e_depois_de_cada_pagina()
    {
        var pages = new[] { CpfCardLines, CpfCardLines, CpfCardLines };
        var document = NewDocument(pageCount: 3);
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(pages), document);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Equal([(0, 3), (1, 3), (2, 3), (3, 3)], rig.Queue.Heartbeats);
        Assert.Equal(3, rig.Store.Progress.Count(entry => entry.EventType == DocumentEventTypes.OcrPageCompleted));
        Assert.Single(rig.Store.Completed);
    }

    [Fact]
    public async Task Nunca_grava_conteudo_do_documento_nos_eventos_da_linha_do_tempo()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines));

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var everyDetail = rig.Store.Stages.Select(stage => stage.Details)
            .Concat(rig.Store.Progress.Select(entry => entry.Details))
            .Append(rig.Store.Completed.Single().Details)
            .Where(detail => detail is not null)
            .ToArray();

        Assert.All(everyDetail, detail =>
        {
            Assert.DoesNotContain("111.444.777-35", detail!);
            Assert.DoesNotContain("MARIA", detail!);
            Assert.DoesNotContain("14/03/1985", detail!);
        });
    }

    [Fact]
    public async Task Documento_sem_evidencia_de_tipo_completa_como_unknown_e_sem_campos()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(["CONTRATO DE PRESTACAO DE SERVICOS", "CLAUSULA PRIMEIRA"]));

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var completed = Assert.Single(rig.Store.Completed);
        Assert.Equal("UNKNOWN", completed.DetectedType);
        Assert.Null(completed.Confidence);
        Assert.Empty(completed.Extraction.Fields);
        Assert.Null(completed.Extraction.StructuredResultJson);
        Assert.Contains("CONTRATO DE PRESTACAO DE SERVICOS", completed.Extraction.RawText);

        // No extractor means no EXTRACTING stage.
        Assert.DoesNotContain(rig.Store.Stages, stage => stage.Stage == DocumentStatus.Extracting);
    }

    [Fact]
    public async Task Divergencia_entre_tipo_esperado_e_identificado_fica_registrada_e_nao_e_corrigida()
    {
        var document = NewDocument(expectedType: "BR_CNPJ_CARD");
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines), document);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var completed = Assert.Single(rig.Store.Completed);
        Assert.Equal("BR_CPF_CARD", completed.DetectedType);
        Assert.Contains("expected=BR_CNPJ_CARD", completed.Details);
        Assert.Contains("divergent=true", completed.Details);
    }

    [Fact]
    public async Task Servico_de_ocr_fora_do_ar_agenda_nova_tentativa_e_nao_completa_nada()
    {
        var provider = new ScriptedOcrProvider
        {
            Behaviour = (_, _, _, _) => throw new OcrProviderException(
                "OCR_UNAVAILABLE",
                "The OCR service could not be reached.",
                isTransient: true)
        };
        var rig = BuildRig(provider);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var (_, error) = Assert.Single(rig.Queue.Failures);
        Assert.Equal("OCR_UNAVAILABLE", error.Code);
        Assert.True(error.IsTransient);
        Assert.Empty(rig.Store.Completed);
    }

    [Fact]
    public async Task Arquivo_ilegivel_falha_de_vez_sem_nova_tentativa()
    {
        var provider = new ScriptedOcrProvider
        {
            Behaviour = (_, _, _, _) => throw new OcrProviderException(
                "UNREADABLE_FILE",
                "The OCR service could not read this file.",
                isTransient: false)
        };
        var rig = BuildRig(provider);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var (_, error) = Assert.Single(rig.Queue.Failures);
        Assert.Equal("UNREADABLE_FILE", error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Falha_no_meio_do_documento_nao_deixa_resultado_parcial()
    {
        var provider = new ScriptedOcrProvider
        {
            Behaviour = async (document, _, progress, ct) =>
            {
                await progress!.OnPageCompletedAsync(new OcrPageProgress(1, 3, TimeSpan.FromSeconds(1), 12), ct);
                throw new OcrProviderException("OCR_UNAVAILABLE", "Lost the OCR service on page 2.", isTransient: true);
            }
        };
        var rig = BuildRig(provider, NewDocument(pageCount: 3));

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Single(rig.Queue.Failures);
        Assert.Empty(rig.Store.Completed);
        Assert.DoesNotContain(rig.Store.Stages, stage => stage.Stage == DocumentStatus.Classifying);
    }

    [Fact]
    public async Task Excecao_inesperada_vira_falha_transitoria_sem_vazar_a_mensagem_da_excecao()
    {
        var provider = new ScriptedOcrProvider
        {
            Behaviour = (_, _, _, _) => throw new InvalidOperationException("CPF 111.444.777-35 quebrou o parser")
        };
        var rig = BuildRig(provider);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var (_, error) = Assert.Single(rig.Queue.Failures);
        Assert.Equal("PROCESSING_ERROR", error.Code);
        Assert.True(error.IsTransient);
        Assert.DoesNotContain("111.444.777-35", error.Message);
    }

    [Fact]
    public async Task Original_ausente_no_storage_falha_de_vez()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines), originalExists: false);

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var (_, error) = Assert.Single(rig.Queue.Failures);
        Assert.Equal("ORIGINAL_MISSING", error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Perder_a_reserva_no_heartbeat_para_tudo_sem_falhar_nem_completar()
    {
        var provider = ScriptedOcrProvider.ReadingPages(CpfCardLines, CpfCardLines);
        var rig = BuildRig(provider, NewDocument(pageCount: 2));

        // The heartbeat before any work is answered, the one after page 1 is refused.
        rig.Queue.HeartbeatsBeforeLockIsLost = 1;

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Empty(rig.Queue.Failures);
        Assert.Empty(rig.Store.Completed);
        Assert.Empty(rig.Queue.Released);
        Assert.DoesNotContain(rig.Store.Stages, stage => stage.Stage == DocumentStatus.Classifying);
    }

    [Fact]
    public async Task Perder_a_reserva_na_hora_de_gravar_descarta_o_resultado_sem_falhar_o_job()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines));
        rig.Store.CompleteResult = false;

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Empty(rig.Queue.Failures);
    }

    [Fact]
    public async Task Falha_reportada_por_quem_perdeu_a_reserva_nao_quebra_o_processador()
    {
        var provider = new ScriptedOcrProvider
        {
            Behaviour = (_, _, _, _) => throw new OcrProviderException("OCR_UNAVAILABLE", "down", isTransient: true)
        };
        var rig = BuildRig(provider);
        rig.Queue.FailIsApplied = false;

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        Assert.Single(rig.Queue.Failures);
        Assert.Empty(rig.Store.Completed);
    }

    [Fact]
    public async Task Desligamento_limpo_devolve_o_job_sem_falhar()
    {
        using var stopping = new CancellationTokenSource();

        var provider = new ScriptedOcrProvider
        {
            Behaviour = async (_, _, _, ct) =>
            {
                await stopping.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return null!;
            }
        };
        var rig = BuildRig(provider);

        await rig.Processor.ProcessAsync(rig.Job, stopping.Token);

        Assert.Same(rig.Job, Assert.Single(rig.Queue.Released));
        Assert.Empty(rig.Queue.Failures);
        Assert.Empty(rig.Store.Completed);
    }

    [Fact]
    public async Task Passa_ao_provedor_o_timeout_por_pagina_e_um_correlation_id()
    {
        var rig = BuildRig(ScriptedOcrProvider.ReadingPages(CpfCardLines));

        await rig.Processor.ProcessAsync(rig.Job, TestContext.Current.CancellationToken);

        var options = rig.Provider.ReceivedOptions!;
        Assert.Equal(new OcrProviderOptions().PageTimeout, options.Timeout);
        Assert.Equal(32, options.CorrelationId!.Length);
    }

    /// <summary>Storage that serves a few bytes for any key, or reports the original as missing.</summary>
    private sealed class StubStorage(bool exists) : IFileStorage
    {
        public Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct) =>
            exists
                ? Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))
                : throw new FileNotFoundException("missing", storageKey);

        public Task DeleteAsync(string storageKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> IsWritableAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
