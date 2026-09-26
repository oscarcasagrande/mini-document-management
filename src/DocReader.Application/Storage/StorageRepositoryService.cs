using System.Text.Json;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Storage;

/// <summary>
/// Administration of the storage repositories. Three rules hold at all times: exactly one repository is the default,
/// the default is active, and a repository documents live in is never deleted or repointed under them. The connection
/// settings are secrets: they are encrypted before they are stored and never returned.
/// </summary>
public sealed class StorageRepositoryService(
    IStorageRepositoryStore store,
    ISecretProtector protector,
    TimeProvider timeProvider,
    ILogger<StorageRepositoryService> logger)
{
    public Task<PagedResult<StorageRepository>> ListAsync(StorageRepositoryFilter filter, CancellationToken ct) =>
        store.ListAsync(filter, ct);

    public async Task<StorageRepository> GetAsync(Guid id, CancellationToken ct) =>
        await store.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("storage-repository", id.ToString());

    public async Task<StorageRepository> CreateAsync(
        string? code,
        string? name,
        StorageProvider provider,
        JsonObject? connectionConfig,
        bool isDefault,
        bool active,
        CancellationToken ct)
    {
        var normalized = StorageRepository.NormalizeCode(code)
            ?? throw new RequestValidationException(
                "INVALID_STORAGE_REPOSITORY_CODE",
                $"The code must have 1 to {StorageRepository.MaxCodeLength} letters, digits, dots, hyphens or underscores, starting with a letter or digit.");
        var cleanName = ValidateName(name);

        StorageConnectionConfig.Validate(provider, connectionConfig);

        if (isDefault)
        {
            RequireCanBeDefault(provider, active);
        }

        if (await store.FindByCodeAsync(normalized, ct).ConfigureAwait(false) is not null)
        {
            throw new ResourceConflictException(
                "STORAGE_REPOSITORY_CODE_EXISTS",
                $"A storage repository with the code {normalized} already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var repository = StorageRepository.Create(
            Guid.CreateVersion7(now),
            normalized,
            cleanName,
            provider,
            Protect(connectionConfig),
            isDefault: false,
            active,
            now);

        await store.AddAsync(repository, makeDefault: isDefault, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Storage repository created. storageRepositoryId={StorageRepositoryId} code={Code} provider={Provider} isDefault={IsDefault}",
            repository.Id,
            repository.Code,
            repository.Provider,
            isDefault);

        return repository;
    }

    /// <summary>
    /// Changes a repository. <paramref name="isDefault"/> null leaves it as is; true makes this the default (the previous one
    /// stops being it); false is refused for the current default, because another has to be made default instead.
    /// <paramref name="connectionConfigPatch"/> is merged into the stored settings: a key sets it, null removes it, the
    /// rest stays.
    /// </summary>
    public async Task<StorageRepository> UpdateAsync(
        Guid id,
        string? name,
        bool active,
        bool? isDefault,
        JsonObject? connectionConfigPatch,
        CancellationToken ct)
    {
        var repository = await GetAsync(id, ct).ConfigureAwait(false);
        var cleanName = ValidateName(name);
        var now = timeProvider.GetUtcNow();

        if (repository.IsDefault && (isDefault == false || !active))
        {
            throw new ResourceConflictException(
                "DEFAULT_STORAGE_REPOSITORY_REQUIRED",
                "This is the default storage repository and there must always be an active one. Make another repository the default first.");
        }

        var becomesDefault = isDefault == true && !repository.IsDefault;
        if (becomesDefault)
        {
            RequireCanBeDefault(repository.Provider, active);
        }

        if (connectionConfigPatch is { Count: > 0 })
        {
            await ApplyConnectionConfigAsync(repository, connectionConfigPatch, now, ct).ConfigureAwait(false);
        }

        repository.Update(cleanName, active, now);
        await store.SaveChangesAsync(ct).ConfigureAwait(false);

        if (becomesDefault)
        {
            await store.SetDefaultAsync(repository.Id, now, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Storage repository updated. storageRepositoryId={StorageRepositoryId} active={Active} isDefault={IsDefault}",
            repository.Id,
            repository.Active,
            becomesDefault || repository.IsDefault);

        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var repository = await GetAsync(id, ct).ConfigureAwait(false);

        if (repository.IsDefault)
        {
            throw new ResourceConflictException(
                "DEFAULT_STORAGE_REPOSITORY_REQUIRED",
                "The default storage repository cannot be deleted. Make another repository the default first.");
        }

        if (await store.IsReferencedAsync(id, ct).ConfigureAwait(false))
        {
            throw new ResourceConflictException(
                "STORAGE_REPOSITORY_IN_USE",
                "Documents are stored in this repository, or a product or service uses it. Deactivate it (active = false) to stop new uploads, or move the products to another repository.");
        }

        await store.RemoveAsync(repository, ct).ConfigureAwait(false);

        logger.LogInformation("Storage repository deleted. storageRepositoryId={StorageRepositoryId}", id);
    }

    private async Task ApplyConnectionConfigAsync(
        StorageRepository repository,
        JsonObject patch,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var current = repository.EncryptedConnectionConfig is null
            ? null
            : JsonNode.Parse(protector.Unprotect(repository.EncryptedConnectionConfig)) as JsonObject;

        var merged = StorageConnectionConfig.Merge(current, patch);
        StorageConnectionConfig.Validate(repository.Provider, merged);

        // The directory is where the files are: pointing it elsewhere would strand every document already stored.
        if (!string.Equals(StorageConnectionConfig.DirectoryOf(current), StorageConnectionConfig.DirectoryOf(merged), StringComparison.Ordinal)
            && await store.HasDocumentsAsync(repository.Id, ct).ConfigureAwait(false))
        {
            throw new ResourceConflictException(
                "STORAGE_REPOSITORY_IN_USE",
                "Documents are already stored in this repository, so its directory cannot change. Create another repository for the new directory.");
        }

        repository.ReplaceConnectionConfig(Protect(merged), now);
    }

    private string? Protect(JsonObject? config) =>
        config is null || config.Count == 0
            ? null
            : protector.Protect(config.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static void RequireCanBeDefault(StorageProvider provider, bool active)
    {
        if (!StorageRepository.IsProviderImplemented(provider))
        {
            throw new UnprocessableRequestException(
                "STORAGE_PROVIDER_NOT_IMPLEMENTED",
                $"The {provider} provider is registered but its adapter is not implemented yet, so it cannot be the default repository.");
        }

        if (!active)
        {
            throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_INACTIVE",
                "An inactive repository cannot be the default.");
        }
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new RequestValidationException("INVALID_STORAGE_REPOSITORY_NAME", "The name is required.");
        }

        return trimmed.Length > StorageRepository.MaxNameLength
            ? throw new RequestValidationException(
                "INVALID_STORAGE_REPOSITORY_NAME",
                $"The name must not exceed {StorageRepository.MaxNameLength} characters.")
            : trimmed;
    }
}
