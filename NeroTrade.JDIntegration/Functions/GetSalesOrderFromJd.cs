using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using System.Net;
using System.Text.Json;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Manually fetches everything JD has stored for a specific sales order, by its Uniconta SO number.
/// Read-only inspection endpoint: it matches on the same "SO {n}" reference used for upsert/delete dedup,
/// so it returns every request order for that SO regardless of status (including cancelled ones), with
/// human-readable status/stage labels plus the full raw JD record.
///
/// Access via: GET /api/get-sales-order-from-jd/{soNumber}
/// </summary>
public sealed class GetSalesOrderFromJd(
    IJdLogisticsService jd,
    ILogger<GetSalesOrderFromJd> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Function("GetSalesOrderFromJd")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "get-sales-order-from-jd/{soNumber}")] HttpRequestData req,
        string soNumber)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("GetSalesOrderFromJd started for SO {SoNumber}", soNumber);

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
                                JdOrderHelper.GetOrderNumberString(o.shopOrderId, o.trackingNote, o.text, o.deliveryNoteText),
                                target,
                                StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(o => o.id)
                .Select(o => new
                {
                    requestOrderId = o.id,
                    statusCode = o.status,
                    status = JdRequestOrderStatus.Describe(o.status),
                    stageCode = o.stage,
                    stage = JdRequestOrderStage.Describe(o.stage),
                    details = o
                })
                .ToList();

            logger.LogInformation("GetSalesOrderFromJd matched {Count} request order(s) for SO {SoNumber}", matches.Count, target);

            if (matches.Count == 0)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound,
                    new { soNumber = target, inventoryId = inventory.id, matched = 0, orders = matches });
            }

            return await WriteJsonAsync(req, HttpStatusCode.OK,
                new { soNumber = target, inventoryId = inventory.id, matched = matches.Count, orders = matches });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetSalesOrderFromJd failed for SO {SoNumber}", soNumber);
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
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
