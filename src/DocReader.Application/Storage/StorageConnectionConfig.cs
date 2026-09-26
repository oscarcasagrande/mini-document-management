using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocReader.Application.Errors;
using DocReader.Domain.Storage;

namespace DocReader.Application.Storage;

/// <summary>
/// The rules for the connection settings of a repository, per provider, and how a partial update is merged. The
/// settings are a flat object of strings: what each key means is fixed by the provider.
/// </summary>
public static partial class StorageConnectionConfig
{
    public const string DirectoryKey = "directory";

    private static readonly IReadOnlyDictionary<StorageProvider, (string[] Required, string[] Optional)> Keys =
        new Dictionary<StorageProvider, (string[] Required, string[] Optional)>
        {
            [StorageProvider.FileSystem] = ([], [DirectoryKey]),
            [StorageProvider.Database] = ([], []),
            [StorageProvider.AzureBlobStorage] = (["connectionString", "container"], []),
            [StorageProvider.AwsS3] = (["bucket", "accessKeyId", "secretAccessKey"], ["region", "serviceUrl"])
        };

    /// <summary>The keys a provider accepts, for the messages.</summary>
    public static string Describe(StorageProvider provider) =>
        Keys[provider] is var (required, optional) && required.Length + optional.Length > 0
            ? $"Required: {(required.Length == 0 ? "none" : string.Join(", ", required))}. Optional: {(optional.Length == 0 ? "none" : string.Join(", ", optional))}."
            : "This provider takes no settings.";

    /// <summary>Applies a partial update: a key with a string sets it, a key with null removes it, the others stay.</summary>
    public static JsonObject Merge(JsonObject? current, JsonObject patch)
    {
        var merged = current is null ? [] : (JsonObject)current.DeepClone();

        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                merged.Remove(key);
            }
            else
            {
                merged[key] = value.DeepClone();
            }
        }

        return merged;
    }

    /// <summary>Checks the settings against the provider: known keys only, string values, required keys present, a safe directory.</summary>
    /// <exception cref="RequestValidationException">The settings are not valid for the provider.</exception>
    public static void Validate(StorageProvider provider, JsonObject? config)
    {
        var (required, optional) = Keys[provider];
        var known = required.Concat(optional).ToArray();
        var settings = config ?? [];

        foreach (var (key, value) in settings)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid(provider, $"The setting '{key}' does not exist for this provider.");
            }

            if (value is not JsonValue json || !json.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            {
                throw Invalid(provider, $"The setting '{key}' must be a non-empty string.");
            }
        }

        foreach (var key in required.Where(key => !settings.ContainsKey(key)))
        {
            throw Invalid(provider, $"The setting '{key}' is required.");
        }

        if (settings[DirectoryKey] is JsonValue directory && directory.TryGetValue<string>(out var path) && !IsSafeDirectory(path))
        {
            throw Invalid(
                provider,
                "The directory must be relative to the storage root, made of letters, digits, dots, hyphens and underscores separated by '/', with no '..'.");
        }
    }

    /// <summary>A relative path of plain segments: it can only ever resolve under the storage root.</summary>
    public static bool IsSafeDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && SafeDirectory().IsMatch(directory)
        && !directory.Split('/').Any(segment => segment is "." or "..");

    public static string? DirectoryOf(JsonObject? config) =>
        config?[DirectoryKey] is JsonValue value && value.TryGetValue<string>(out var directory) ? directory : null;

    private static RequestValidationException Invalid(StorageProvider provider, string reason) =>
        new("INVALID_CONNECTION_CONFIG", $"{reason} {Describe(provider)}");

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeDirectory();
}
