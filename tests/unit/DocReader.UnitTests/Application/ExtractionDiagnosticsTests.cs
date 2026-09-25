using System.Text.Json;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Extraction;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// O diagnóstico de extração sobre o OCR gravado: o que foi lido, por que um campo falhou e o que a regra
/// procurou. Os casos com blocos usam as fixtures reais mascaradas, que têm o mesmo formato do payload gravado.
/// </summary>
public sealed class ExtractionDiagnosticsTests
{
    private static readonly DateTimeOffset Now = Stage3Support.Now;

    private static readonly ExtractionSummary Summary = new(
        Guid.NewGuid(), "paddleocr", "PP-OCRv5 test", "rules-1.0.0", "br-cnh-1.0.0", 1, 0.9m, Now);

    private static DocumentQueryService QueryService(InMemoryDocumentStore store) =>
        new(
            store,
            new InMemoryFileStorage(),
            new RulesDocumentClassifier(),
            [new BrCnhExtractor(new FakeTimeProvider(Now)), new BrCinExtractor(new FakeTimeProvider(Now))],
            NullLogger<DocumentQueryService>.Instance);

    private static (InMemoryDocumentStore Store, Document Document) Stored(string? detectedType, string pageText, string? rawOcr = null)
    {
        var store = new InMemoryDocumentStore();
        var id = Guid.CreateVersion7(Now);

        var document = Document.Accept(
            id, "DOC-20260925-000101", "doc.png", $"documents/{id:D}/original.png", "image/png",
            2048, new string('d', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkQueued(Now);
        store.AcceptAsync(document, ProcessingJob.CreateForDocument(id, Now), null, CancellationToken.None).GetAwaiter().GetResult();

        if (detectedType is not null)
        {
            document.RecordClassification(detectedType, 0.9m, Now);
        }

        document.MarkCompleted(Now);
        store.Jobs.Clear();

        var pages = JsonSerializer.Serialize(new[] { new { pageNumber = 1, text = pageText } });
        store.Extractions[document.Id] = (new ExtractionResultView(Summary, []), new ExtractionTextView(Summary, pages));

        if (rawOcr is not null)
        {
            store.RawOcr[document.Id] = rawOcr;
        }

        return (store, document);
    }

    /// <summary>A fixture tem o formato do payload gravado (página e blocos com coordenadas) e o texto de página que o acompanha.</summary>
    private static (string Raw, string Text) Fixture(string name)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr", $"{name}.ocr.json"));
        using var document = JsonDocument.Parse(raw);
        var text = string.Join(
            '\n',
            document.RootElement.GetProperty("pages")[0].GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("text").GetString()));

        return (raw, text);
    }

    private static async Task<ExtractionDiagnostics> Diagnose(InMemoryDocumentStore store, Document document) =>
        (await QueryService(store).GetExtractionDiagnosticsAsync(document.Id, TestContext.Current.CancellationToken)).Diagnostics;

    [Fact]
    public async Task Cnh_real_traz_os_blocos_com_coordenadas_e_a_cobertura()
    {
        var (raw, text) = Fixture("cnh-real");
        var (store, document) = Stored("BR_CNH", text, raw);

        var diagnostics = await Diagnose(store, document);

        Assert.Equal("BR_CNH", diagnostics.DocumentType);
        Assert.Equal("RECORDED", diagnostics.DocumentTypeSource);
        Assert.Equal("BLOCKS", diagnostics.OcrInput);
        Assert.Null(diagnostics.Note);

        var blocks = Assert.Single(diagnostics.Pages).Blocks;
        Assert.Equal(68, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(8, block.BoundingBox.Count));
        Assert.Equal(Enumerable.Range(0, blocks.Count), blocks.Select(block => block.Index));

        Assert.Equal(8, diagnostics.Coverage.Expected);
        Assert.Equal(7, diagnostics.Coverage.Extracted);
        Assert.True(diagnostics.Coverage.ExtractedRatio >= 0.7m);
    }

    [Fact]
    public async Task Campo_lido_mostra_o_rotulo_que_casou_e_o_candidato_aceito()
    {
        var (raw, text) = Fixture("cnh-real");
        var (store, document) = Stored("BR_CNH", text, raw);

        var name = (await Diagnose(store, document)).Fields.Single(field => field.Path == "name");

        Assert.Equal("VALID", name.Status);
        Assert.Equal("FOUND", name.Reason);
        Assert.Equal("NOME SOCIAL TESTE CENTO E DEZ", name.Normalized);

        var rule = Assert.IsType<DiagnosedRule>(name.Rule);
        Assert.Empty(rule.Labels.Single(label => label.Label == "NOME").LineIndexes);
        Assert.NotEmpty(rule.Labels.Single(label => label.Label == "NOME E SOBRENOME").LineIndexes);
        Assert.Contains(rule.Candidates, candidate => candidate.Accepted);
    }

    [Fact]
    public async Task Rotulo_achado_e_valor_nao_lido_pelo_ocr_e_valor_rejeitado_e_nao_rotulo_ausente()
    {
        // A letra da categoria não foi lida pelo OCR: o rótulo "9 CAT. HAB." existe, o valor não.
        var (raw, text) = Fixture("cnh-real");
        var (store, document) = Stored("BR_CNH", text, raw);

        var category = (await Diagnose(store, document)).Fields.Single(field => field.Path == "category");

        // Sem nada sob o rótulo, a regra cai na ordem de leitura e examina vizinhos que não são o valor.
        Assert.Equal("NOT_FOUND", category.Status);
        Assert.Equal(ExtractionReasons.ValueRejected, category.Reason);
        Assert.Contains("none had the expected format", category.Explanation, StringComparison.Ordinal);
        Assert.Contains(category.Rule!.Labels, label => label.LineIndexes.Count > 0);
        Assert.All(category.Rule.Candidates, candidate => Assert.False(candidate.Accepted));
    }

    [Fact]
    public async Task Sem_nenhum_rotulo_no_texto_o_motivo_e_rotulo_nao_encontrado_com_a_lista_tentada()
    {
        var (store, document) = Stored("BR_CNH", "REPUBLICA FEDERATIVA DO BRASIL\nCARTEIRA NACIONAL DE HABILITACAO");

        var registration = (await Diagnose(store, document)).Fields.Single(field => field.Path == "registrationNumber");

        Assert.Equal(ExtractionReasons.LabelNotFound, registration.Reason);
        Assert.Contains("None of the labels was found", registration.Explanation, StringComparison.Ordinal);
        Assert.Contains("N REGISTRO", registration.Explanation, StringComparison.Ordinal);
        Assert.All(registration.Rule!.Labels, label => Assert.Empty(label.LineIndexes));
    }

    [Fact]
    public async Task Valor_lido_que_reprova_na_validacao_e_validation_failed_com_o_valor_preservado()
    {
        var (raw, text) = Fixture("ric-real");
        var (store, document) = Stored("BR_CIN", text, raw);

        var cpf = (await Diagnose(store, document)).Fields.Single(field => field.Path == "cpf");

        Assert.Equal("INVALID", cpf.Status);
        Assert.Equal(ExtractionReasons.ValidationFailed, cpf.Reason);
        Assert.Equal("123.456.789-39", cpf.Raw);
        Assert.Contains("CHECK_DIGIT_INVALID", cpf.Messages);
    }

    [Fact]
    public async Task Ric_real_atinge_a_cobertura_minima_de_setenta_por_cento()
    {
        var (raw, text) = Fixture("ric-real");
        var (store, document) = Stored("BR_CIN", text, raw);

        var diagnostics = await Diagnose(store, document);

        Assert.Equal(10, diagnostics.Coverage.Expected);
        Assert.True(diagnostics.Coverage.ExtractedRatio >= 0.7m, $"cobertura {diagnostics.Coverage.ExtractedRatio}");
    }

    [Fact]
    public async Task Status_gravado_aparece_ao_lado_do_de_agora()
    {
        var (raw, text) = Fixture("cnh-real");
        var (store, document) = Stored("BR_CNH", text, raw);
        store.Extractions[document.Id] = (
            new ExtractionResultView(
                Summary,
                [new ExtractedFieldView("name", null, null, null, null, "[]", "NOT_FOUND", "[]")]),
            store.Extractions[document.Id].Text);

        var name = (await Diagnose(store, document)).Fields.Single(field => field.Path == "name");

        Assert.Equal("VALID", name.Status);
        Assert.Equal("NOT_FOUND", name.RecordedStatus);
    }

    [Fact]
    public async Task Extracao_gravada_sem_blocos_roda_sem_coordenadas_e_avisa()
    {
        var (store, document) = Stored("BR_CNH", "CARTEIRA NACIONAL DE HABILITACAO\nVALIDADE\n23/05/2030");

        var diagnostics = await Diagnose(store, document);

        Assert.Equal("PAGE_TEXT", diagnostics.OcrInput);
        Assert.Contains("Reprocess", diagnostics.Note, StringComparison.Ordinal);
        Assert.All(diagnostics.Pages.Single().Blocks, block => Assert.Empty(block.BoundingBox));
    }

    [Fact]
    public async Task Documento_unknown_sem_tipo_reconhecivel_nao_tem_extrator_e_diz_por_que()
    {
        var (store, document) = Stored(null, "Bom dia, segue em anexo o relatorio.");

        var diagnostics = await Diagnose(store, document);

        Assert.Null(diagnostics.DocumentType);
        Assert.Empty(diagnostics.Fields);
        Assert.Contains("UNKNOWN", diagnostics.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documento_gravado_como_unknown_usa_a_classificacao_de_agora()
    {
        var (raw, text) = Fixture("cnh-real");
        var (store, document) = Stored("UNKNOWN", text, raw);

        var diagnostics = await Diagnose(store, document);

        Assert.Equal("BR_CNH", diagnostics.DocumentType);
        Assert.Equal("CURRENT_CLASSIFICATION", diagnostics.DocumentTypeSource);
        Assert.NotEmpty(diagnostics.Fields);
    }

    [Fact]
    public async Task Documento_sem_extracao_e_conflito_com_o_status_atual()
    {
        var store = new InMemoryDocumentStore();
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, "DOC-20260925-000102", "doc.png", $"documents/{id:D}/original.png", "image/png",
            2048, new string('e', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkQueued(Now);
        await store.AcceptAsync(document, ProcessingJob.CreateForDocument(id, Now), null, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<ResultNotReadyException>(() =>
            QueryService(store).GetExtractionDiagnosticsAsync(id, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentStatus.Queued, error.Status);
    }

    [Fact]
    public async Task Documento_inexistente_e_not_found()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            QueryService(new InMemoryDocumentStore()).GetExtractionDiagnosticsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extracao_com_rastro_devolve_o_mesmo_resultado_que_sem_rastro()
    {
        var extractor = new BrCinExtractor(new FakeTimeProvider(Now));
        var ocr = Stage3Support.Fixture("ric-real");

        var plain = await extractor.ExtractAsync(ocr, TestContext.Current.CancellationToken);
        var traced = await extractor.ExtractAsync(ocr, new ExtractionTrace(), TestContext.Current.CancellationToken);

        Assert.Equal(plain.Fields.Keys.Order(), traced.Fields.Keys.Order());
        foreach (var (path, value) in plain.Fields)
        {
            Assert.Equal(value.ValidationStatus, traced.Fields[path].ValidationStatus);
            Assert.Equal(value.Normalized, traced.Fields[path].Normalized);
            Assert.Equal(value.Confidence, traced.Fields[path].Confidence);
        }
    }
}
