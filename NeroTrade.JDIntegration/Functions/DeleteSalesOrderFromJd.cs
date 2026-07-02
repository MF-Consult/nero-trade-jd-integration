using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using System.Net;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Manually deletes a specific sales order (JD request order) from JD by its Uniconta SO number.
/// Matches on the same "SO {n}" reference used for upsert dedup, so it finds the order regardless of
/// its JD status — including cancelled orders that must be fully removed before a new one can be uploaded.
/// Like every JD write, deletion respects DryRun: with DryRun on it is logged and skipped (the call
/// reports success but nothing is removed). Turn DryRun off to perform a real manual deletion.
///
/// Access via: DELETE or POST /api/delete-sales-order-from-jd/{soNumber}
/// </summary>
public sealed class DeleteSalesOrderFromJd(
    IJdLogisticsService jd,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<DeleteSalesOrderFromJd> logger)
{
    [Function("DeleteSalesOrderFromJd")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", "post", Route = "delete-sales-order-from-jd/{soNumber}")] HttpRequestData req,
        string soNumber)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("DeleteSalesOrderFromJd started for SO {SoNumber}", soNumber);

        try
        {
            if (string.IsNullOrWhiteSpace(soNumber))
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { error = "soNumber is required." });

            var target = soNumber.Trim();

            var inventories = await jd.GetInventoriesAsync(cts.Token);
            var inventory = inventories.FirstOrDefault();
            if (inventory?.id == null)
            {
                logger.LogWarning("No inventories available in JD");
                return await WriteJsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = "No inventories available in JD." });
            }

            // Find every request order whose "SO {n}" reference matches, regardless of status (incl. cancelled).
            var orders = await jd.GetRequestOrdersAsync(inventory.id.Value, cts.Token);
            var matches = orders
                .Where(o => o.id.HasValue &&
                            string.Equals(
                                JdOrderHelper.GetOrderNumberString(o.shopOrderId, o.text, o.deliveryNoteText),
                                target,
                                StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                logger.LogInformation("No JD request orders matched SO {SoNumber}", target);
                return await WriteJsonAsync(req, HttpStatusCode.NotFound,
                    new { soNumber = target, matched = 0, deleted = 0, message = "No matching request order found in JD." });
            }

            var results = new List<object>(matches.Count);
            var deleted = 0;
            foreach (var order in matches)
            {
                var (ok, status, message) = await jd.DeleteRequestOrderAsync(inventory.id.Value, order.id!.Value, cts.Token);
                if (ok) deleted++;
                results.Add(new { requestOrderId = order.id, jdStatus = order.status, ok, httpStatus = status, message });
                logger.LogInformation("Delete JD request order {Id} for SO {SoNumber}: ok={Ok} httpStatus={Status}",
                    order.id, target, ok, status);
            }

            var allDeleted = deleted == matches.Count;
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, allDeleted ? "info" : "warning", "JD", target,
                $"DeleteSalesOrderFromJd: {deleted}/{matches.Count} request order(s) deleted for SO {target}.",
                null,
                JsonSerializer.SerializeToElement(new { soNumber = target, matched = matches.Count, deleted, results })
            ), cts.Token);

            // 200 when every match was removed; 207 (MultiStatus) when some deletions failed.
            var responseStatus = allDeleted ? HttpStatusCode.OK : HttpStatusCode.MultiStatus;
            return await WriteJsonAsync(req, responseStatus,
                new { soNumber = target, matched = matches.Count, deleted, results });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteSalesOrderFromJd failed for SO {SoNumber}", soNumber);
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "JD", soNumber,
                $"DeleteSalesOrderFromJd failed for SO {soNumber}: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { soNumber, error = ex.Message });
        }
    }

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object payload)
    {
        // Set the status at creation time (before the response starts) and write JSON manually.
        // The ASP.NET Core integration pre-populates a Content-Type header, so WriteAsJsonAsync would
        // try to ADD a second one ("Content-Type does not support multiple values") and then fail again
        // when the status is set after the response has started. Setting both explicitly avoids both.
        var response = req.CreateResponse(status);
        response.Headers.Remove("Content-Type");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload));
        return response;
    }
}
