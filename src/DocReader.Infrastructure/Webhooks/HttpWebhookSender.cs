using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Webhooks;

/// <summary>
/// Sends a webhook with an HTTP POST. The body goes exactly as stored (it is what the signature covers). Redirects are not
/// followed: a receiver that answers 3xx has not accepted the notification, and following it would let a URL bounce the request to
/// somewhere the subscription never named. The answer body is never read.
/// </summary>
public sealed class HttpWebhookSender(IHttpClientFactory clientFactory, IOptions<WebhookOptions> options) : IWebhookSender
{
    public const string ClientName = "webhooks";

    public async Task<WebhookSendResult> SendAsync(WebhookRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.RequestTimeout);

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Url) { Content = new ByteArrayContent(request.Body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.UserAgent.ParseAdd("DocReader-Webhook/1.0");
        message.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, request.Signature);
        message.Headers.TryAddWithoutValidation("X-Webhook-Event", request.Event);
        message.Headers.TryAddWithoutValidation("X-Webhook-Delivery", request.DeliveryId.ToString("D"));
        message.Headers.TryAddWithoutValidation("X-Webhook-Attempt", request.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var client = clientFactory.CreateClient(ClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            return new WebhookSendResult((int)response.StatusCode, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookSendResult(null, "TIMEOUT");
        }
        catch (HttpRequestException exception) when (exception.InnerException is BlockedAddressException)
        {
            return new WebhookSendResult(null, "BLOCKED_ADDRESS");
        }
        catch (HttpRequestException)
        {
            return new WebhookSendResult(null, "CONNECTION_FAILED");
        }
    }

    /// <summary>
    /// Connects to the host, but only to an address that may be reached. The check is on the addresses the name resolves to, at the
    /// moment of connecting, so a name that points at a private address (or changes to one between the check and the
    /// connection) is refused as well.
    /// </summary>
    public static async ValueTask<Stream> ConnectGuardedAsync(SocketsHttpConnectionContext context, bool allowPrivateNetworks, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);

        var allowed = addresses.Where(address => allowPrivateNetworks || !PrivateNetwork.IsPrivate(address)).ToArray();
        if (allowed.Length == 0)
        {
            throw new BlockedAddressException(host);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>The host resolves only to addresses webhooks may not reach.</summary>
public sealed class BlockedAddressException(string host) : IOException($"The host {host} resolves only to local or private addresses.");
