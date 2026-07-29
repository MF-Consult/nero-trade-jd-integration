using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;

namespace NeroTrade.JDIntegration.Functions.Admin;

/// <summary>
/// Operator-grade remediation endpoints called by the Hermes agent (or a human via Slack approval).
/// Each endpoint reuses an existing <see cref="IUnicontaService"/> method — no new business logic lives
/// here. The endpoints are gated by a shared-secret header (<c>X-Remediation-Secret</c>) bound from
/// <see cref="RemediationOptions"/>; if the secret is not configured, every endpoint refuses the call so
/// the surface cannot accidentally be exposed unauthenticated.
///
/// <para><b>Route prefix is <c>remediation/</c>, never <c>admin/</c>.</b> The Functions host reserves
/// <c>/admin/*</c> for its own management API (<c>/admin/host/status</c>, <c>/admin/functions/...</c>),
/// so all three of these functions failed to register with "The specified route conflicts with one or
/// more built in routes" — silently, at every host start, from the day they were added (2026-06-27) until
/// 2026-07-27. They were never callable in production. Do not move them back under <c>admin/</c>.</para>
/// </summary>
public sealed class RemediationEndpoints(
    IUnicontaService uniconta,
    IIntegrationLogger integrationLogger,
    RemediationOptions remediationOptions,
    ILogger<RemediationEndpoints> logger)
{
    private const string SecretHeader = "X-Remediation-Secret";

    [Function("RetrySalesOrder")]
    public Task<HttpResponseData> RetrySalesOrderAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "remediation/retry-sales-order/{soNumber:int}")] HttpRequestData req,
        int soNumber,
        CancellationToken cancellationToken)
        => HandleAsync(req, cancellationToken, async logScope =>
        {
            var success = await uniconta.SetSalesOrderStatusAsync(soNumber, string.Empty, new Dictionary<string, object>
            {
                [UnicontaUserFields.IntegrationIssue] = string.Empty,
                [UnicontaUserFields.SalesOrderTransferFlag] = true,
            }, cancellationToken);

            await WriteAuditAsync(
                level: success ? "info" : "error",
                sourceSystem: "Uniconta",
                externalId: soNumber.ToString(),
                action: "retry-sales-order",
                success: success,
                details: new { soNumber },
                logScope,
                cancellationToken);

            return (success, new { action = "retry-sales-order", soNumber, applied = success });
        });

    [Function("RetryPurchaseOrder")]
    public Task<HttpResponseData> RetryPurchaseOrderAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "remediation/retry-purchase-order/{poNumber:int}")] HttpRequestData req,
        int poNumber,
        CancellationToken cancellationToken)
        => HandleAsync(req, cancellationToken, async logScope =>
        {
            var success = UnicontaWriteResult.Updated == await uniconta.SetPurchaseOrderHeaderFieldsAsync(poNumber, new Dictionary<string, object>
            {
                [UnicontaUserFields.PurchaseOrderJdStatus] = PurchaseOrderJdStatusValues.ManualHandling,
                [UnicontaUserFields.PurchaseOrderTransferFlag] = true,
            }, cancellationToken);

            await WriteAuditAsync(
                level: success ? "info" : "error",
                sourceSystem: "Uniconta",
                externalId: poNumber.ToString(),
                action: "retry-purchase-order",
                success: success,
                details: new { poNumber },
                logScope,
                cancellationToken);

            return (success, new { action = "retry-purchase-order", poNumber, applied = success });
        });

    [Function("OverrideOrderStatus")]
    public Task<HttpResponseData> OverrideOrderStatusAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "remediation/override-order-status/{orderNumber:int}")] HttpRequestData req,
        int orderNumber,
        CancellationToken cancellationToken)
        => HandleAsync(req, cancellationToken, async logScope =>
        {
            var body = await JsonSerializer.DeserializeAsync<OverrideStatusBody>(req.Body, JsonOpts, cancellationToken);
            if (body is null || string.IsNullOrWhiteSpace(body.Group))
            {
                return (false, new { action = "override-order-status", orderNumber, error = "Body must include non-empty 'group'." });
            }

            var success = await uniconta.UpdateSalesOrderGroupAsync(orderNumber, body.Group, cancellationToken);

            await WriteAuditAsync(
                level: success ? "info" : "error",
                sourceSystem: "Uniconta",
                externalId: orderNumber.ToString(),
                action: "override-order-status",
                success: success,
                details: new { orderNumber, group = body.Group },
                logScope,
                cancellationToken);

            return (success, new { action = "override-order-status", orderNumber, group = body.Group, applied = success });
        });

    private async Task<HttpResponseData> HandleAsync(
        HttpRequestData req,
        CancellationToken cancellationToken,
        Func<IntegrationLogScope, Task<(bool success, object body)>> action)
    {
        if (!IsAuthorized(req, out var authError))
        {
            logger.LogWarning("Remediation endpoint rejected: {Reason}", authError);
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync(authError, cancellationToken);
            return unauth;
        }

        var logScope = new IntegrationLogScope();
        try
        {
            var (success, body) = await action(logScope);
            var response = req.CreateResponse(success ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOpts), cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Remediation endpoint threw");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "Integration", null,
                $"Remediation endpoint failed: {LogSanitizer.Describe(ex)}", null, null)
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "REMEDIATION_FAILED",
                Retryable = false,
                SuggestedAction = "Inspect stack trace in App Insights — remediation primitive threw before completing."
            }, CancellationToken.None);

            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync("Remediation failed; see integration_logs.", cancellationToken);
            return error;
        }
    }

    private bool IsAuthorized(HttpRequestData req, out string error)
    {
        if (string.IsNullOrWhiteSpace(remediationOptions.SharedSecret))
        {
            error = "Remediation endpoints are disabled: Remediation:SharedSecret is not configured.";
            return false;
        }

        if (!req.Headers.TryGetValues(SecretHeader, out var values))
        {
            error = $"Missing {SecretHeader} header.";
            return false;
        }

        var provided = values.FirstOrDefault();
        if (!FixedTimeEquals(provided, remediationOptions.SharedSecret))
        {
            error = "Invalid remediation secret.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task WriteAuditAsync(
        string level,
        string sourceSystem,
        string externalId,
        string action,
        bool success,
        object details,
        IntegrationLogScope logScope,
        CancellationToken cancellationToken)
    {
        await integrationLogger.LogAsync(new IntegrationLogEntry(
            integrationLogger.IntegrationName,
            level,
            sourceSystem,
            externalId,
            success ? $"Remediation '{action}' applied for {externalId}." : $"Remediation '{action}' failed for {externalId}.",
            null,
            JsonSerializer.SerializeToElement(details))
        {
            CorrelationId = logScope.CorrelationId,
            ErrorCode = success ? "REMEDIATION_APPLIED" : "REMEDIATION_NOOP",
            Retryable = !success,
            SuggestedAction = success
                ? "No further action — Uniconta side mutated; next scheduled tick will resync to JD."
                : "Inspect Uniconta field-level errors; the entity may be locked or fields may not exist."
        }, cancellationToken);
    }

    // Constant-time compare to avoid leaking secret length via early exit.
    private static bool FixedTimeEquals(string? provided, string expected)
    {
        if (provided is null) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(provided);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record OverrideStatusBody(string? Group);
}
