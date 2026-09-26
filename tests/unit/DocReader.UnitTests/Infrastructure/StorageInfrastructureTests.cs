using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>The encryption of the connection settings, the factory of adapters and the providers that are not implemented.</summary>
public sealed class StorageInfrastructureTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docreader-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static AesGcmSecretProtector Protector(string? key = null) =>
        new(Options.Create(new SecretsOptions { EncryptionKey = key ?? NewKey() }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- cifra -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cifra_e_reversivel_e_o_envelope_nao_mostra_o_conteudo()
    {
        var protector = Protector();
        const string plain = "{\"connectionString\":\"AccountKey=SEGREDO-ABC\",\"container\":\"docs\"}";

        var envelope = protector.Protect(plain);

        Assert.DoesNotContain("SEGREDO", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("container", envelope, StringComparison.Ordinal);
        Assert.Equal(plain, protector.Unprotect(envelope));
    }

    [Fact]
    public void O_envelope_e_json_valido_para_caber_em_jsonb()
    {
        using var document = JsonDocument.Parse(Protector().Protect("{\"a\":\"b\"}"));

        Assert.Equal(1, document.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("AES-256-GCM", document.RootElement.GetProperty("alg").GetString());
    }

    [Fact]
    public void O_mesmo_texto_cifrado_duas_vezes_da_envelopes_diferentes()
    {
        var protector = Protector();

        Assert.NotEqual(protector.Protect("{\"a\":\"b\"}"), protector.Protect("{\"a\":\"b\"}"));
    }

    [Fact]
    public void Outra_chave_nao_decifra_e_a_mensagem_nao_traz_o_conteudo()
    {
        var envelope = Protector().Protect("{\"connectionString\":\"SEGREDO-ABC\"}");

        var error = Assert.Throws<InvalidOperationException>(() => Protector().Unprotect(envelope));

        Assert.DoesNotContain("SEGREDO", error.Message, StringComparison.Ordinal);
        Assert.Contains("encryption key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_adulterado_e_recusado()
    {
        var key = NewKey();
        var protector = Protector(key);
        var node = JsonNode.Parse(protector.Protect("{\"a\":\"b\"}"))!.AsObject();
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
        var result = new SecretsOptionsValidator().Validate(null, new SecretsOptions { EncryptionKey = key });

        Assert.True(result.Failed);
        Assert.Contains("32 bytes", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Chave_de_32_bytes_e_aceita()
    {
        Assert.True(new SecretsOptionsValidator().Validate(null, new SecretsOptions { EncryptionKey = NewKey() }).Succeeded);
    }

    // ---- fábrica de adapters --------------------------------------------------------------------------------------

    private StorageAdapterFactory Factory()
    {
        // No connection is opened: the context is only handed to the database adapter.
        var context = new DocReaderDbContext(
            new DbContextOptionsBuilder<DocReaderDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

        return new StorageAdapterFactory(context, Options.Create(new StorageOptions { RootPath = _root }), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Filesystem_sem_diretorio_guarda_na_raiz_configurada()
    {
        var adapter = Factory().Create(StorageProvider.FileSystem, null);
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        await using var content = new MemoryStream([1, 2, 3]);
        var stored = await adapter.SaveAsync(content, metadata, Ct);

        Assert.True(File.Exists(Path.Combine(_root, stored.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Filesystem_com_diretorio_guarda_dentro_dele_e_so_dentro_da_raiz()
    {
        var adapter = Factory().Create(StorageProvider.FileSystem, new JsonObject { ["directory"] = "clientes/acme" });
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        await using var content = new MemoryStream([1, 2, 3]);
        var stored = await adapter.SaveAsync(content, metadata, Ct);

        Assert.True(File.Exists(Path.Combine(_root, "clientes", "acme", stored.StorageKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("../fora")]
    [InlineData("/etc")]
    [InlineData("a/../../fora")]
    [InlineData("C:\\dados")]
    public void Diretorio_que_escapa_da_raiz_nunca_vira_um_adapter_mesmo_que_a_validacao_tenha_falhado(string directory)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Factory().Create(StorageProvider.FileSystem, new JsonObject { ["directory"] = directory }));
    }

    [Fact]
    public void A_fabrica_devolve_o_adapter_de_cada_provedor()
    {
        var factory = Factory();

        Assert.IsType<FileSystemStorageAdapter>(factory.Create(StorageProvider.FileSystem, null));
        Assert.IsType<DatabaseStorageAdapter>(factory.Create(StorageProvider.Database, null));
        Assert.IsType<AzureBlobStorageAdapter>(factory.Create(StorageProvider.AzureBlobStorage, null));
        Assert.IsType<AwsS3StorageAdapter>(factory.Create(StorageProvider.AwsS3, null));
    }

    [Fact]
    public async Task Azure_e_s3_estao_registrados_mas_toda_operacao_e_not_implemented()
    {
        var metadata = new FileMetadata(Guid.CreateVersion7(Now), ".pdf", "application/pdf", Now);

        foreach (var adapter in new IStorageAdapter[] { new AzureBlobStorageAdapter(), new AwsS3StorageAdapter() })
        {
            await using var content = new MemoryStream([1]);
            await Assert.ThrowsAsync<NotImplementedException>(() => adapter.SaveAsync(content, metadata, Ct));
            await Assert.ThrowsAsync<NotImplementedException>(() => adapter.OpenReadAsync("k", Ct));
            await Assert.ThrowsAsync<NotImplementedException>(() => adapter.DeleteAsync("k", Ct));
            await Assert.ThrowsAsync<NotImplementedException>(() => adapter.IsWritableAsync(Ct));
        }
    }

    [Fact]
    public void Os_providers_implementados_sao_so_filesystem_e_database()
    {
        Assert.True(StorageRepository.IsProviderImplemented(StorageProvider.FileSystem));
        Assert.True(StorageRepository.IsProviderImplemented(StorageProvider.Database));
        Assert.False(StorageRepository.IsProviderImplemented(StorageProvider.AzureBlobStorage));
        Assert.False(StorageRepository.IsProviderImplemented(StorageProvider.AwsS3));
    }
}
