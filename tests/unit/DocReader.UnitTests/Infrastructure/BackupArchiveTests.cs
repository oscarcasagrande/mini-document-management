using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Backup;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>
/// The backup archive format with the real builder and reader, no database: what goes in, and every way an archive that
/// was altered, corrupted, truncated, forged or made elsewhere is refused before anything would restore it.
/// </summary>
public sealed class BackupArchiveTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "docreader-unit-backup", Guid.NewGuid().ToString("N"));
    private readonly SecretsOptions _secrets = new() { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    private IOptions<BackupOptions> Options(long maxExtracted = 20L * 1024 * 1024 * 1024) =>
        Microsoft.Extensions.Options.Options.Create(new BackupOptions { WorkingDirectory = _work, MaxExtractedBytes = maxExtracted });

    private TarGzBackupArchiveReader Reader(SecretsOptions? secrets = null, long maxExtracted = 20L * 1024 * 1024 * 1024) =>
        new(new BackupSignature(Microsoft.Extensions.Options.Options.Create(secrets ?? _secrets)), Options(maxExtracted));

    private static BackupManifestEntry Entry(StorageProvider provider) => new(
        Guid.CreateVersion7(Now), Guid.CreateVersion7(Now), $"documents/2026/09/29/{Guid.NewGuid():D}/original.pdf", provider, "application/pdf", Now, null);

    private async Task<byte[]> BuildAsync(string sql, IReadOnlyList<BackupManifestEntry> manifest, Dictionary<Guid, byte[]>? files = null)
    {
        var builder = new TarGzBackupArchiveBuilder(
            new ScriptDumper(sql, manifest),
            new BackupSignature(Microsoft.Extensions.Options.Options.Create(_secrets)),
            Options(),
            new FakeTimeProvider(Now));

        await using var archive = await builder.BuildAsync(
            (entry, _) => Task.FromResult<Stream?>(files is not null && files.TryGetValue(entry.DocumentId, out var bytes) ? new MemoryStream(bytes) : null),
            Ct);

        await using var stream = archive.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);

        Assert.Equal(buffer.Length, archive.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())), archive.ChecksumSha256);

        return buffer.ToArray();
    }

    private static List<(string Name, byte[] Content)> Entries(byte[] archive)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var result = new List<(string, byte[])>();

        while (tar.GetNextEntry() is { } entry)
        {
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            result.Add((entry.Name, buffer.ToArray()));
        }

        return result;
    }

    private static byte[] Pack(IEnumerable<(string Name, byte[] Content)> entries, TarEntryType type = TarEntryType.RegularFile)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, content) in entries)
            {
                var entry = new PaxTarEntry(type, name);
                if (type == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream(content);
                }

                tar.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private async Task<BackupArchiveInvalidException> RefusedAsync(byte[] archive, TarGzBackupArchiveReader? reader = null) =>
        await Assert.ThrowsAsync<BackupArchiveInvalidException>(async () =>
        {
            await using var extracted = await (reader ?? Reader()).ExtractAndVerifyAsync(new MemoryStream(archive), Ct);
        });

    [Fact]
    public async Task O_arquivo_tem_dump_manifesto_arquivos_locais_e_checksums_no_formato_do_sha256sum()
    {
        var local = Entry(StorageProvider.FileSystem);
        var cloud = Entry(StorageProvider.AwsS3);
        var bytes = RandomNumberGenerator.GetBytes(1000);

        var archive = await BuildAsync("SELECT 1;", [local, cloud], new() { [local.DocumentId] = bytes });
        var entries = Entries(archive).ToDictionary(entry => entry.Name, entry => entry.Content);

        Assert.Equal(
            ["checksums.sha256", "checksums.sha256.hmac", "data.sql", $"files/{local.DocumentId:D}", "storage_manifest.json"],
            entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(bytes, entries[$"files/{local.DocumentId:D}"]);

        var checksums = Encoding.UTF8.GetString(entries["checksums.sha256"]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, checksums.Length);
        Assert.Contains($"{Convert.ToHexStringLower(SHA256.HashData(entries["data.sql"]))}  data.sql", checksums);
        Assert.Contains($"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  files/{local.DocumentId:D}", checksums);

        var manifest = Encoding.UTF8.GetString(entries["storage_manifest.json"]);
        Assert.Contains("\"formatVersion\": 1", manifest, StringComparison.Ordinal);
        Assert.Contains("\"storageProvider\": \"AWS_S3\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Um_arquivo_integro_e_lido_de_volta_com_o_manifesto()
    {
        var local = Entry(StorageProvider.Database);
        var cloud = Entry(StorageProvider.AzureBlobStorage);
        var bytes = RandomNumberGenerator.GetBytes(300);
        var archive = await BuildAsync("SELECT 42;", [local, cloud], new() { [local.DocumentId] = bytes });

        await using var extracted = await Reader().ExtractAndVerifyAsync(new MemoryStream(archive), Ct);

        Assert.Equal("SELECT 42;", await File.ReadAllTextAsync(extracted.DatabaseDumpPath, Ct));
        Assert.Equal(2, extracted.Documents.Count);
        Assert.Equal(local with { ArchivePath = $"files/{local.DocumentId:D}" }, extracted.Documents.Single(entry => entry.DocumentId == local.DocumentId));
        Assert.Null(extracted.Documents.Single(entry => entry.DocumentId == cloud.DocumentId).ArchivePath);

        await using var file = await extracted.OpenArchivedFileAsync($"files/{local.DocumentId:D}", Ct);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, Ct);
        Assert.Equal(bytes, buffer.ToArray());

        await Assert.ThrowsAsync<FileNotFoundException>(() => extracted.OpenArchivedFileAsync("../etc/passwd", Ct));
    }

    [Fact]
    public async Task Um_byte_alterado_no_dump_e_recusado_por_checksum()
    {
        var entries = Entries(await BuildAsync("SELECT 1;", []));
        var tampered = entries.Select(entry => entry.Name == "data.sql" ? (entry.Name, Encoding.UTF8.GetBytes("SELECT 2;")) : entry);

        var refused = await RefusedAsync(Pack(tampered));

        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", refused.ErrorCode);
        Assert.Contains("data.sql", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checksums_refeitos_sem_a_chave_sao_recusados_pela_assinatura()
    {
        var entries = Entries(await BuildAsync("SELECT 1;", []));
        var evil = Encoding.UTF8.GetBytes("DROP TABLE documents;");
        var manifest = entries.Single(entry => entry.Name == "storage_manifest.json").Content;
        var forgedChecksums = Encoding.UTF8.GetBytes(
            $"{Convert.ToHexStringLower(SHA256.HashData(evil))}  data.sql\n{Convert.ToHexStringLower(SHA256.HashData(manifest))}  storage_manifest.json\n");

        var forged = entries.Select(entry => entry.Name switch
        {
            "data.sql" => (entry.Name, evil),
            "checksums.sha256" => (entry.Name, forgedChecksums),
            _ => entry
        });

        Assert.Equal("BACKUP_SIGNATURE_INVALID", (await RefusedAsync(Pack(forged))).ErrorCode);
    }

    [Fact]
    public async Task Arquivo_de_outra_instalacao_com_outra_chave_e_recusado()
    {
        var archive = await BuildAsync("SELECT 1;", []);
        var otherKey = new SecretsOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

        Assert.Equal("BACKUP_SIGNATURE_INVALID", (await RefusedAsync(archive, Reader(otherKey))).ErrorCode);
    }

    [Fact]
    public async Task Arquivo_sem_checksums_e_recusado()
    {
        var entries = Entries(await BuildAsync("SELECT 1;", [])).Where(entry => entry.Name != "checksums.sha256");

        Assert.Equal("BACKUP_CHECKSUMS_MISSING", (await RefusedAsync(Pack(entries))).ErrorCode);
    }

    [Fact]
    public async Task Arquivo_listado_que_falta_ou_arquivo_a_mais_nao_listado_sao_recusados()
    {
        var local = Entry(StorageProvider.FileSystem);
        var entries = Entries(await BuildAsync("SELECT 1;", [local], new() { [local.DocumentId] = [1, 2, 3] }));

        var missing = entries.Where(entry => !entry.Name.StartsWith("files/", StringComparison.Ordinal));
        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", (await RefusedAsync(Pack(missing))).ErrorCode);

        var extra = entries.Append(("files/intruso", [9, 9]));
        Assert.Equal("BACKUP_CHECKSUM_MISMATCH", (await RefusedAsync(Pack(extra))).ErrorCode);
    }

    [Fact]
    public async Task Entrada_com_caminho_para_fora_ou_link_e_recusada_antes_de_extrair()
    {
        var entries = Entries(await BuildAsync("SELECT 1;", []));

        var traversal = entries.Append(("../../escapou.sql", [1]));
        Assert.Equal("BACKUP_ARCHIVE_INVALID", (await RefusedAsync(Pack(traversal))).ErrorCode);

        var absolute = entries.Append(("/etc/cron.d/x", [1]));
        Assert.Equal("BACKUP_ARCHIVE_INVALID", (await RefusedAsync(Pack(absolute))).ErrorCode);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "data.sql") { LinkName = "/etc/passwd" });
        }

        Assert.Equal("BACKUP_ARCHIVE_INVALID", (await RefusedAsync(output.ToArray())).ErrorCode);
        Assert.False(Directory.Exists(_work) && Directory.EnumerateFileSystemEntries(_work).Any(), "The workspace of a refused archive must be removed.");
    }

    [Fact]
    public async Task Arquivo_que_nao_e_tar_gz_ou_esta_truncado_e_recusado()
    {
        Assert.Equal("BACKUP_ARCHIVE_UNREADABLE", (await RefusedAsync(Encoding.UTF8.GetBytes("isto não é um tar.gz"))).ErrorCode);

        var archive = await BuildAsync("SELECT 1;", []);
        Assert.Equal("BACKUP_ARCHIVE_UNREADABLE", (await RefusedAsync(archive[..(archive.Length / 2)])).ErrorCode);
    }

    [Fact]
    public async Task Arquivo_que_expande_alem_do_limite_e_recusado()
    {
        var archive = await BuildAsync(new string('x', 1024 * 1024 + 10), []);

        Assert.Equal("BACKUP_ARCHIVE_TOO_LARGE", (await RefusedAsync(archive, Reader(maxExtracted: 1024 * 1024))).ErrorCode);
    }

    [Fact]
    public void Caminhos_do_arquivo_so_aceitam_nomes_relativos_simples()
    {
        Assert.True(BackupArchiveLayout.IsSafeRelativePath("data.sql"));
        Assert.True(BackupArchiveLayout.IsSafeRelativePath("files/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath("../data.sql"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath("files/../../x"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath("/abs"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath("files//x"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath("files\\x"));
        Assert.False(BackupArchiveLayout.IsSafeRelativePath(string.Empty));
    }
}
