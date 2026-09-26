using DocReader.Domain.Documents;
using DocReader.Domain.Retention;
using Xunit;

namespace DocReader.UnitTests.Domain;

/// <summary>A precedência entre políticas e o prazo que elas dão a um documento.</summary>
public sealed class RetentionPolicyResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProductA = Guid.CreateVersion7(Now);
    private static readonly Guid ProductB = Guid.CreateVersion7(Now.AddSeconds(1));

    private static RetentionPolicy Policy(string? type, Guid? product, int days) =>
        RetentionPolicy.Create(Guid.CreateVersion7(Now), type, product, days, Now);

    private static readonly RetentionPolicy Global = RetentionPolicy.Create(RetentionPolicy.GlobalPolicyId, null, null, 365, Now);

    [Fact]
    public void Politica_especifica_de_tipo_e_produto_vence_todas()
    {
        var both = Policy("BR_CNH", ProductA, 10);
        var policies = new[] { Global, Policy("BR_CNH", null, 20), Policy(null, ProductA, 30), both };

        Assert.Same(both, RetentionPolicyResolver.Resolve(policies, "BR_CNH", ProductA));
    }

    [Fact]
    public void Politica_do_produto_vence_a_do_tipo()
    {
        var byProduct = Policy(null, ProductA, 30);
        var policies = new[] { Global, Policy("BR_CNH", null, 20), byProduct };

        Assert.Same(byProduct, RetentionPolicyResolver.Resolve(policies, "BR_CNH", ProductA));
    }

    [Fact]
    public void Politica_do_tipo_vence_a_global_quando_nao_ha_do_produto()
    {
        var byType = Policy("BR_CNH", null, 20);
        var policies = new[] { Global, byType, Policy(null, ProductB, 30) };

        Assert.Same(byType, RetentionPolicyResolver.Resolve(policies, "BR_CNH", ProductA));
    }

    [Fact]
    public void Sem_nenhuma_especifica_vale_a_global()
    {
        var policies = new[] { Global, Policy("BR_CIN", null, 20), Policy(null, ProductB, 30) };

        Assert.Same(Global, RetentionPolicyResolver.Resolve(policies, "BR_CNH", ProductA));
    }

    [Fact]
    public void Documento_sem_produto_ignora_politicas_de_produto()
    {
        var byType = Policy("BR_CNH", null, 20);
        var policies = new[] { Global, byType, Policy(null, ProductA, 30), Policy("BR_CNH", ProductA, 10) };

        Assert.Same(byType, RetentionPolicyResolver.Resolve(policies, "BR_CNH", null));
    }

    [Fact]
    public void Documento_sem_tipo_usa_a_do_produto_ou_a_global()
    {
        var byProduct = Policy(null, ProductA, 30);
        var policies = new[] { Global, Policy("BR_CNH", null, 20), Policy("BR_CNH", ProductA, 10), byProduct };

        Assert.Same(byProduct, RetentionPolicyResolver.Resolve(policies, null, ProductA));
        Assert.Same(Global, RetentionPolicyResolver.Resolve(policies, null, null));
    }

    [Fact]
    public void Politica_de_tipo_e_produto_nao_vale_para_so_um_dos_dois()
    {
        var both = Policy("BR_CNH", ProductA, 10);
        var policies = new[] { Global, both };

        Assert.Same(Global, RetentionPolicyResolver.Resolve(policies, "BR_CNH", ProductB));
        Assert.Same(Global, RetentionPolicyResolver.Resolve(policies, "BR_CIN", ProductA));
        Assert.Same(Global, RetentionPolicyResolver.Resolve(policies, "BR_CNH", null));
    }

    [Fact]
    public void Sem_politica_nenhuma_o_resultado_e_nulo()
    {
        Assert.Null(RetentionPolicyResolver.Resolve([], "BR_CNH", ProductA));
    }

    [Theory]
    [InlineData("BR_CNH", "ProductA", RetentionScope.DocumentTypeAndProductService)]
    [InlineData(null, "ProductA", RetentionScope.ProductService)]
    [InlineData("BR_CNH", null, RetentionScope.DocumentType)]
    [InlineData(null, null, RetentionScope.Global)]
    public void Escopo_e_deduzido_dos_dois_eixos(string? type, string? product, RetentionScope expected)
    {
        var policy = Policy(type, product is null ? null : ProductA, 10);

        Assert.Equal(expected, policy.Scope);
        Assert.Equal(expected == RetentionScope.Global, policy.IsGlobal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(36_501)]
    public void Dias_fora_do_intervalo_sao_recusados(int days)
    {
        Assert.False(RetentionPolicy.IsValidDays(days));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy("BR_CNH", null, days));
    }

    [Fact]
    public void Alterar_a_duracao_nao_muda_o_escopo()
    {
        var policy = Policy("BR_CNH", ProductA, 10);

        policy.ChangeRetention(50, Now.AddDays(1));

        Assert.Equal(50, policy.RetentionDays);
        Assert.Equal(RetentionScope.DocumentTypeAndProductService, policy.Scope);
        Assert.Equal(Now.AddDays(1), policy.UpdatedAt);
    }

    // ---- o prazo no documento ----------------------------------------------------------------------------

    private static Document NewDocument(RetentionPolicy? policy = null)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, "DOC-20260925-000001", "a.png", $"documents/{id:D}/original.png", "image/png", 10, new string('a', 64), 1,
            UploadChannel.Api, null, null, Now, null, policy);
        document.MarkQueued(Now);

        return document;
    }

    [Fact]
    public void Documento_aceito_com_politica_recebe_a_data_de_expurgo_a_partir_do_upload()
    {
        var document = NewDocument(Policy("BR_CNH", null, 30));

        Assert.Equal(Now.AddDays(30), document.ExpiresAt);
        Assert.Equal(30, document.RetentionDays);
        Assert.NotNull(document.RetentionPolicyId);
    }

    [Fact]
    public void Documento_aceito_sem_politica_nao_tem_data()
    {
        Assert.Null(NewDocument().ExpiresAt);
    }

    [Fact]
    public void Reaplicar_mantem_o_inicio_da_contagem_e_troca_so_a_duracao()
    {
        var document = NewDocument(Policy(null, null, 30));
        var shorter = Policy("BR_CNH", null, 10);

        document.ReapplyRetention(shorter);

        Assert.Equal(Now.AddDays(10), document.ExpiresAt);
        Assert.Equal(shorter.Id, document.RetentionPolicyId);
        Assert.Equal(10, document.RetentionDays);
    }

    [Fact]
    public void Reaplicar_num_documento_antigo_sem_data_conta_a_partir_do_upload()
    {
        var document = NewDocument();

        document.ReapplyRetention(Policy(null, null, 15));

        Assert.Equal(Now.AddDays(15), document.ExpiresAt);
    }

    [Fact]
    public void Aplicar_de_novo_recomeca_a_contagem_no_instante_dado()
    {
        var document = NewDocument(Policy(null, null, 30));

        document.ApplyRetention(Policy(null, null, 30), Now.AddDays(100));

        Assert.Equal(Now.AddDays(130), document.ExpiresAt);
    }

    [Theory]
    [InlineData(DocumentStatus.Completed, true)]
    [InlineData(DocumentStatus.Failed, true)]
    [InlineData(DocumentStatus.Rejected, true)]
    [InlineData(DocumentStatus.Queued, false)]
    [InlineData(DocumentStatus.Stored, false)]
    [InlineData(DocumentStatus.OcrRunning, false)]
    [InlineData(DocumentStatus.Purged, false)]
    public void So_documento_em_estado_final_e_vencido_e_expurgavel(DocumentStatus status, bool purgeable)
    {
        var document = NewDocument(Policy(null, null, 10));

        switch (status)
        {
            case DocumentStatus.Completed: document.MarkCompleted(Now); break;
            case DocumentStatus.Failed: document.MarkFailed("X", "y", Now); break;
            case DocumentStatus.Rejected: SetStatus(document, DocumentStatus.Rejected); break;
            case DocumentStatus.Purged: document.MarkCompleted(Now); document.MarkPurged(Now); break;
            case DocumentStatus.Stored: SetStatus(document, DocumentStatus.Stored); break;
            case DocumentStatus.OcrRunning: document.AdvanceTo(DocumentStatus.OcrRunning, "OCR_STARTED", Now); break;
        }

        Assert.Equal(purgeable, document.IsPurgeable(Now.AddDays(11)));
    }

    [Fact]
    public void Antes_da_data_o_documento_nao_e_expurgavel_e_na_data_exata_e()
    {
        var document = NewDocument(Policy(null, null, 10));
        document.MarkCompleted(Now);

        Assert.False(document.IsPurgeable(Now.AddDays(10).AddTicks(-1)));
        Assert.True(document.IsPurgeable(Now.AddDays(10)));
    }

    [Fact]
    public void Expurgar_registra_o_evento_uma_unica_vez()
    {
        var document = NewDocument(Policy(null, null, 10));
        document.MarkCompleted(Now);

        document.MarkPurged(Now.AddDays(11));
        document.MarkPurged(Now.AddDays(12));

        Assert.Equal(DocumentStatus.Purged, document.Status);
        Assert.Equal(Now.AddDays(11), document.PurgedAt);
        Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.Purged);
    }

    private static void SetStatus(Document document, DocumentStatus status) =>
        typeof(Document).GetProperty(nameof(Document.Status))!.SetValue(document, status);
}
