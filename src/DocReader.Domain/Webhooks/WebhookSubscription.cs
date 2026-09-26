using DocReader.Domain.Catalog;

namespace DocReader.Domain.Webhooks;

/// <summary>
/// Someone who wants to be told when a document reaches a final state. The notification is a signed POST: the body is
/// signed with the secret of the subscription (HMAC-SHA256), so the receiver can tell it came from here. The secret is
/// stored encrypted and is never returned.
/// </summary>
public sealed class WebhookSubscription
{
    public const int MaxUrlLength = 2048;
    public const int MinimumSecretLength = 16;
    public const int MaximumSecretLength = 256;

    private WebhookSubscription()
    {
    }

    public Guid Id { get; private init; }

    public string Url { get; private set; } = string.Empty;

    /// <summary>The HMAC secret as an encrypted envelope; the dispatcher decrypts it to sign each delivery.</summary>
    public string EncryptedSecret { get; private set; } = string.Empty;

    /// <summary>Event names (see <see cref="WebhookEvents"/>) this subscription receives. Never empty.</summary>
    public string[] Events { get; private set; } = [];

    /// <summary>When set, only documents of this product or service are notified; null means every document.</summary>
    public Guid? ProductServiceId { get; private set; }

    public ProductService? ProductService { get; private set; }

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static WebhookSubscription Create(
        Guid id,
        string url,
        string encryptedSecret,
        IEnumerable<string> events,
        Guid? productServiceId,
        bool active,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedSecret);

        return new WebhookSubscription
        {
            Id = id,
            Url = url,
            EncryptedSecret = encryptedSecret,
            Events = Normalize(events),
            ProductServiceId = productServiceId,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Update(string url, IEnumerable<string> events, Guid? productServiceId, bool active, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        Url = url;
        Events = Normalize(events);
        ProductServiceId = productServiceId;
        Active = active;
        UpdatedAt = now;
    }

    public void ReplaceSecret(string encryptedSecret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedSecret);

        EncryptedSecret = encryptedSecret;
        UpdatedAt = now;
    }

    /// <summary>Whether a document of <paramref name="productServiceId"/> that reached <paramref name="webhookEvent"/> is for this subscription.</summary>
    public bool Wants(string webhookEvent, Guid? productServiceId) =>
        Active
        && Events.Contains(webhookEvent, StringComparer.Ordinal)
        && (ProductServiceId is null || ProductServiceId == productServiceId);

    private static string[] Normalize(IEnumerable<string> events) =>
        [.. events.Select(name => name.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
