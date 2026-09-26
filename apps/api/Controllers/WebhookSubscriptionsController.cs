using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// Subscribers that are told, with a signed POST, when a document is completed, fails or is purged.
/// </summary>
/// <remarks>
/// The body of a notification is JSON with <c>event</c>, <c>documentId</c>, <c>protocol</c>, <c>status</c>,
/// <c>detectedDocumentType</c>, <c>productServiceCode</c> and <c>occurredAt</c> (UTC). The header
/// <c>X-Webhook-Signature</c> is <c>sha256=</c> followed by the lower case hexadecimal HMAC-SHA256 of the exact body bytes, keyed
/// with the secret of the subscription. <c>X-Webhook-Event</c>, <c>X-Webhook-Delivery</c> (the same on every retry, for
/// deduplication) and <c>X-Webhook-Attempt</c> come along. Any 2xx counts as delivered; anything else is retried after 10, 30 and
/// 90 seconds, and when the last attempt fails the document gets a <c>WEBHOOK_DELIVERY_FAILED</c> event on its timeline.
/// </remarks>
[ApiController]
[Route("api/v1/webhook-subscriptions")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class WebhookSubscriptionsController(
    WebhookSubscriptionService service,
    IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Creates a webhook subscription.</summary>
    /// <remarks>
    /// The secret is stored encrypted and no endpoint returns it. Send your own (16 to 256 characters) or omit it: a random one is
    /// generated and returned in this answer, and only in this answer. The URL must be public unless the deployment allows private
    /// networks (<c>WEBHOOK_ALLOW_PRIVATE_NETWORKS</c>).
    /// </remarks>
    /// <param name="request">URL, events, optional product filter and optional secret.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="201">Created. The <c>Location</c> header points at it.</response>
    /// <response code="400">The URL, the events or the secret are invalid.</response>
    /// <response code="422">The product or service does not exist.</response>
    [HttpPost]
    [ProducesResponseType(typeof(WebhookSubscriptionCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<WebhookSubscriptionCreatedResponse>> CreateAsync(
        [FromBody] CreateWebhookSubscriptionRequest request,
        CancellationToken ct)
    {
        var created = await service.CreateAsync(request.Url, request.Secret, request.Events, request.ProductServiceId, request.Active, ct);

        return Created(
            $"/api/v1/webhook-subscriptions/{created.Subscription.Id}",
            ConfigurationResponseMapper.ToCreatedResponse(created));
    }

    /// <summary>Lists the webhook subscriptions. The secret is never included.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of subscriptions.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<WebhookSubscriptionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<WebhookSubscriptionResponse>>> ListAsync(
        [FromQuery] WebhookSubscriptionListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new WebhookSubscriptionFilter(request.Active, request.ProductServiceId, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, ConfigurationResponseMapper.ToResponse));
    }

    /// <summary>Returns one webhook subscription. The secret is never included.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The subscription.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WebhookSubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<WebhookSubscriptionResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Replaces the URL, the events, the product filter and the active flag of a subscription.</summary>
    /// <remarks>Send a <c>secret</c> to rotate the signing secret; omit it to keep the current one.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="request">The new values.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Updated.</response>
    /// <response code="400">The URL, the events or the secret are invalid.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="422">The product or service does not exist.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(WebhookSubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<WebhookSubscriptionResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateWebhookSubscriptionRequest request,
        CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(
            await service.UpdateAsync(id, request.Url, request.Secret, request.Events, request.ProductServiceId, request.Active, ct)));

    /// <summary>Deletes a webhook subscription and what was still queued for it.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);

        return NoContent();
    }

    /// <summary>Lists the notifications of a subscription and how their sending went, newest first.</summary>
    /// <remarks>Read-only, for finding out whether a document's notification arrived and, if not, why: status, attempts, the HTTP status the subscriber answered and the failure code.</remarks>
    /// <param name="id">Identity of the subscription.</param>
    /// <param name="page">One based page number.</param>
    /// <param name="pageSize">Items per page.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of deliveries.</response>
    /// <response code="404">There is no subscription with this id.</response>
    [HttpGet("{id:guid}/deliveries")]
    [ProducesResponseType(typeof(PagedResponse<WebhookDeliveryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<PagedResponse<WebhookDeliveryResponse>>> ListDeliveriesAsync(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int? pageSize = null,
        CancellationToken ct = default)
    {
        var paging = pagingOptions.Value;
        var size = Math.Clamp(pageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var deliveries = await service.ListDeliveriesAsync(id, Math.Max(1, page), size, ct);

        return Ok(ConfigurationResponseMapper.ToPage(deliveries, ConfigurationResponseMapper.ToResponse));
    }
}
