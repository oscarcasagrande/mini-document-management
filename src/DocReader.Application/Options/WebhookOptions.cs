using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>Sending of webhook notifications. The worker sends; the API only validates the URLs it is given.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "DocReader:Webhooks";

    /// <summary>
    /// Wait before each retry. The first attempt is immediate; a failure waits the first delay, the next failure the second, and
    /// so on. When the delays run out the delivery is failed for good, so the default (10s, 30s, 90s) is four attempts in all.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90)];

    /// <summary>Budget of one POST, connection and answer included.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the dispatcher looks for a delivery that is due when there is nothing to send.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Silence after which a delivery that a worker took is given to another (the worker died mid-send).</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether a subscription may point at a private, loopback or link-local address (localhost, 10.x, 192.168.x, the Docker
    /// network, the cloud metadata address). Off by default: the worker sits inside the network, and a webhook is a way to make
    /// it send a request to anything the URL names. Turn it on to test against a receiver on your own machine.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Deliveries sent at the same time by one worker.</summary>
    [Range(1, 32)]
    public int Concurrency { get; set; } = 2;
}
