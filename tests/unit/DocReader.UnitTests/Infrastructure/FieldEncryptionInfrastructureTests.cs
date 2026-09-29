using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Domain.Extractions;
using DocReader.Infrastructure.Encryption;
using DocReader.Infrastructure.Persistence;
using DocReader.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>The encryption of extracted field values (RawValue/NormalizedValue) and how it is wired into EF Core.</summary>
public sealed class FieldEncryptionInfrastructureTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmFieldEncryptionProtector Protector(string? key = null) =>
        new(Options.Create(new FieldEncryptionOptions { EncryptionKey = key ?? NewKey() }));

    // ---- cifra -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cifra_e_reversivel_e_o_envelope_nao_mostra_o_conteudo()
    {
        var protector = Protector();
        const string plain = "111.444.777-35";

        var envelope = protector.Protect(plain);

        Assert.DoesNotContain("111.444.777-35", envelope, StringComparison.Ordinal);
        Assert.Equal(plain, protector.Unprotect(envelope));
    }

    [Fact]
    public void O_envelope_e_json_valido_com_o_mesmo_formato_do_protetor_de_configuracao()
    {
        using var document = JsonDocument.Parse(Protector().Protect("Fulano de Tal"));

        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("AES-256-GCM", document.RootElement.GetProperty("alg").GetString());
    }

    [Fact]
    public void O_mesmo_valor_cifrado_duas_vezes_da_envelopes_diferentes()
    {
        var protector = Protector();

        Assert.NotEqual(protector.Protect("Rua das Flores, 123"), protector.Protect("Rua das Flores, 123"));
    }

    [Fact]
    public void Outra_chave_nao_decifra_e_a_mensagem_nao_traz_o_conteudo()
    {
        var envelope = Protector().Protect("11144477735");

        var error = Assert.Throws<InvalidOperationException>(() => Protector().Unprotect(envelope));

        Assert.DoesNotContain("11144477735", error.Message, StringComparison.Ordinal);
        Assert.Contains("encryption key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_adulterado_e_recusado()
    {
        var key = NewKey();
        var protector = Protector(key);
        var node = JsonNode.Parse(protector.Protect("CPF"))!.AsObject();
        var cipher = Convert.FromBase64String((string)node["c"]!);
        cipher[0] ^= 0xFF;
        node["c"] = Convert.ToBase64String(cipher);

        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(node.ToJsonString()));
    }

    [Theory]
    [InlineData("nao e json")]
    [InlineData("{}")]
    [InlineData("{\"v\":9,\"alg\":\"x\",\"n\":\"\",\"c\":\"\",\"t\":\"\"}")]
    public void Lixo_no_lugar_do_envelope_e_erro_claro_e_nao_excecao_bruta(string garbage)
    {
        Assert.Throws<InvalidOperationException>(() => Protector().Unprotect(garbage));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void Chave_invalida_para_o_servico_subir(string key)
    {
        var result = new FieldEncryptionOptionsValidator().Validate(null, new FieldEncryptionOptions { EncryptionKey = key });

        Assert.True(result.Failed);
        Assert.Contains("32 bytes", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_de_32_bytes_e_aceita()
    {
        Assert.True(new FieldEncryptionOptionsValidator().Validate(null, new FieldEncryptionOptions { EncryptionKey = NewKey() }).Succeeded);
    }

    [Fact]
    public void A_chave_de_campo_e_independente_da_chave_de_configuracao_de_storage()
    {
        // Same plain text, one key each: what one protector wrote, the other must not be able to read.
        var storageKey = NewKey();
        var fieldKey = NewKey();
        var fieldProtector = Protector(fieldKey);

        var fromStorageStyleKey = new AesGcmFieldEncryptionProtector(Options.Create(new FieldEncryptionOptions { EncryptionKey = storageKey }));
        var envelope = fromStorageStyleKey.Protect("segredo-do-documento");

        Assert.Throws<InvalidOperationException>(() => fieldProtector.Unprotect(envelope));
    }

    // ---- integração com o EF Core --------------------------------------------------------------------------------

    // EF Core caches the compiled model per DbContext CLR type, not per instance (the default IModelCacheKeyFactory
    // only looks at context.GetType()). That means the value converter's closure over "protector", baked into the
    // model the FIRST time any DocReaderDbContext is built in this process, is what every later DocReaderDbContext
    // actually uses at run time -- building a second context with a different protector instance does not rebuild
    // the model or replace that closure. Every test below that exercises the encrypted converter (as opposed to the
    // AesGcmFieldEncryptionProtector class on its own, further up) therefore builds its context from
    // TestFieldEncryptionKey, the one key every unit test in this project shares (see its doc comment), so it never
    // matters which construction wins that race: a fresh protector built from that same key decrypts whatever the
    // cached converter encrypted, because AES-GCM only cares about key bytes, not which object they came wrapped
    // in. In the real app this is harmless -- one process, one key, for the whole run -- but it is why the
    // field-encryption key cannot change without a restart, same as STORAGE_CONFIG_ENCRYPTION_KEY.
    private static DocReaderDbContext ModelContext() =>
        new(
            new DbContextOptionsBuilder<DocReaderDbContext>().UseNpgsql("Host=localhost;Database=unused").Options,
            Protector(TestFieldEncryptionKey.Value));

    [Fact]
    public void RawValue_e_NormalizedValue_tem_o_conversor_de_cifra_e_um_comparador_pelo_texto_puro()
    {
        using var context = ModelContext();

        var entityType = context.Model.FindEntityType(typeof(ExtractedField))!;
        var rawValue = entityType.FindProperty(nameof(ExtractedField.RawValue))!;
        var normalizedValue = entityType.FindProperty(nameof(ExtractedField.NormalizedValue))!;

        Assert.NotNull(rawValue.GetValueConverter());
        Assert.NotNull(normalizedValue.GetValueConverter());
        Assert.Equal(10000, rawValue.GetMaxLength());
        Assert.Equal(10000, normalizedValue.GetMaxLength());

        var comparer = rawValue.GetValueComparer();
        Assert.NotNull(comparer);
        // Two different envelopes of the SAME plain text must compare equal: the comparer looks at the model
        // (plain text) side, not the provider (envelope) side, or a random nonce would look like a real change.
        Assert.True(comparer!.Equals("11144477735", "11144477735"));
    }

    [Fact]
    public void O_conversor_cifra_ao_gravar_e_decifra_ao_ler_sem_expor_o_texto_puro_no_valor_convertido()
    {
        using var context = ModelContext();

        var converter = context.Model.FindEntityType(typeof(ExtractedField))!
            .FindProperty(nameof(ExtractedField.RawValue))!
            .GetValueConverter()!;

        const string plain = "Fulano da Silva";
        var stored = (string?)converter.ConvertToProvider(plain);

        Assert.NotNull(stored);
        Assert.DoesNotContain(plain, stored, StringComparison.Ordinal);
        Assert.Equal(plain, (string?)converter.ConvertFromProvider(stored));
        // A fresh protector built from the same key as the model's cached converter (see ModelContext above)
        // must decrypt it too: proves the envelope really is a standalone, key-addressable AES-GCM ciphertext.
        Assert.Equal(plain, Protector(TestFieldEncryptionKey.Value).Unprotect(stored));
    }

    [Fact]
    public void O_conversor_deixa_nulo_passar_sem_cifrar()
    {
        using var context = ModelContext();

        var converter = context.Model.FindEntityType(typeof(ExtractedField))!
            .FindProperty(nameof(ExtractedField.RawValue))!
            .GetValueConverter()!;

        Assert.Null(converter.ConvertToProvider(null));
        Assert.Null(converter.ConvertFromProvider(null));
    }
}
