using System.Text.Json.Nodes;
using DocReader.Domain.Storage;

namespace DocReader.Application.Abstractions;

/// <summary>Builds the adapter of a provider from the (decrypted) settings of a repository.</summary>
public interface IStorageAdapterFactory
{
    IStorageAdapter Create(StorageProvider provider, JsonObject? connectionConfig);
}
