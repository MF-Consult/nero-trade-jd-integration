using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.JdWebhook;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Receives JD's outbound webhooks (request-order stage changes, incoming-shipment status changes) and turns
/// each into a "run now" request for the matching JD→Uniconta sync job, so a status or received quantity reaches
/// Uniconta within one dispatcher tick (≤30 s) instead of waiting for the 10/30-minute poll.
///
/// It does no business work itself: the polling job still reads JD and writes Uniconta, so there is a single
/// code path and the slow poll remains the safety net if JD never delivers an event (JD gives no retry
/// guarantees in its docs, and this endpoint rejects unsigned calls).
///
/// Auth: anonymous at the Functions level, authenticated by JD's <c>X-JD-Hash</c> HMAC (see
/// <see cref="JdWebhookSignature"/>). With no <c>JdWebhook__Secret</c> configured every call gets 401.
/// Register once with <c>POST /api/webhooks</c> (types from <see cref="JdWebhookRouter.SubscribedTypes"/>,
/// url = <c>https://nero-trade-data-syncer-d5apejcef0ahbpge.westeurope-01.azurewebsites.net/api/jd/webhook</c>) and store the returned secret.
/// </summary>
public sealed class JdWebhookReceiver(
    SyncScheduler scheduler,
    JdWebhookOptions options,
    IIntegrationLogger integrationLogger,
    ILogger<JdWebhookReceiver> logger)
{
    [Function("JdWebhookReceiver")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jd/webhook/{*rest}")] HttpRequestData req)
    {
        var jobId = FirstHeader(req, "X-JD-Job-Id");
        var hash = FirstHeader(req, "X-JD-Hash");

        if (!JdWebhookSignature.IsValid(jobId, hash, options.Secret))
        {
            logger.LogWarning("Rejected JD webhook call: missing or invalid signature (jobId={JobId})", jobId);
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            var type = ReadInt(doc.RootElement, "WebhookType");
            var name = ReadString(doc.RootElement, "WebhookTypeName");
            var job = type.HasValue ? JdWebhookRouter.JobFor(type.Value) : null;

            // One info row per accepted event, so webhook delivery can be measured against what polling finds
            // (events per day, per type, and which job each one nudged). Best-effort: never fails the call.
            await RecordAsync(name, type, jobId, job);

            if (job != null)
            {
                scheduler.RecordWebhook(job, DateTime.UtcNow);
                scheduler.RequestRunNow(job);
                logger.LogInformation("JD webhook {Name} ({Type}) accepted, job {Job} will run on the next tick (jobId={JobId})", name, type, job, jobId);
            }
            else
            {
                logger.LogInformation("JD webhook {Name} ({Type}) accepted, no job to trigger (jobId={JobId})", name, type, jobId);
            }
        }
        catch (JsonException ex)
        {
            // Authenticated but unreadable: acknowledge anyway. Failing would only make JD retry something we
            // cannot use, and the polling job covers the event.
            logger.LogWarning(ex, "JD webhook body was not valid JSON (jobId={JobId})", jobId);
        }

        return req.CreateResponse(HttpStatusCode.OK);
    }

    private async Task RecordAsync(string? name, int? type, string? jobId, string? job)
    {
        try
        {
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "info", "JD", null,
                $"JD webhook {name ?? "?"} received" + (job != null ? $"; {job} will run on the next tick." : "; no job to trigger."),
                null,
                JsonSerializer.SerializeToElement(new { webhookType = type, webhookTypeName = name, jdJobId = jobId, triggeredJob = job })),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record JD webhook in integration_logs (jobId={JobId})", jobId);
        }
    }

    private static string? FirstHeader(HttpRequestData req, string name)
        => req.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static int? ReadInt(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v))
                return v;
        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        return null;
    }
}
