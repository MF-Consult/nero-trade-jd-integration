using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Read-only inspection: pulls a specific purchase order or sales order straight from Uniconta (ignoring
/// the transfer-flag/status eligibility the sync enforces) and shows BOTH the raw Uniconta projection and
/// the exact JD payload the mappers would produce. Useful for answering "why did this order map like that?"
/// (e.g. speditør/kolli). The JD view deliberately spells out the fields that are <c>[JsonIgnore]</c> on the
/// wire DTOs (<c>Sku</c>, <c>unit</c>, <c>SourcePurchaseNumber</c>) so nothing is hidden from analysis.
///
/// No mutation happens, so this is safe regardless of DryRun. Gated by a function key like
/// <see cref="GetSalesOrderFromJd"/>.
///   GET /api/inspect/purchase-order/{poNumber}
///   GET /api/inspect/sales-order/{soNumber}
/// </summary>
public sealed class InspectOrderFromUniconta(
    IUnicontaService uniconta,
    PurchaseOrderMapper purchaseOrderMapper,
    SalesOrderMapper salesOrderMapper,
    ILogger<InspectOrderFromUniconta> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Function("InspectPurchaseOrderFromUniconta")]
    public async Task<HttpResponseData> InspectPurchaseOrderAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "inspect/purchase-order/{poNumber:int}")] HttpRequestData req,
        int poNumber,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("InspectPurchaseOrderFromUniconta started for PO {PoNumber}", poNumber);
        try
        {
            // An open order is the normal-path source; a booked (bogført) order lives only as a posted
            // invoice (the safety-net source). Check both so any PO can be inspected wherever it sits.
            var openOrder = await uniconta.ReadPurchaseOrderByNumberAsync(poNumber, cancellationToken);
            if (openOrder is not null)
            {
                var jd = purchaseOrderMapper.Map(openOrder);
                return await WriteJsonAsync(req, HttpStatusCode.OK, new
                {
                    poNumber,
                    source = "open-order",           // handled by SyncPurchaseOrdersToJd
                    uniconta = openOrder,
                    jd = DescribeShipment(jd)
                });
            }

            var postedInvoice = await uniconta.ReadPostedPurchaseInvoiceByNumberAsync(poNumber, cancellationToken);
            if (postedInvoice is not null)
            {
                var jd = purchaseOrderMapper.Map(postedInvoice);
                return await WriteJsonAsync(req, HttpStatusCode.OK, new
                {
                    poNumber,
                    source = "posted-invoice",        // handled by SyncPostedPurchaseInvoicesToJd (safety-net)
                    uniconta = postedInvoice,
                    jd = DescribeShipment(jd)
                });
            }

            return await WriteJsonAsync(req, HttpStatusCode.NotFound,
                new { poNumber, source = "not-found", message = "No open purchase order or posted purchase invoice found with that number." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "InspectPurchaseOrderFromUniconta failed for PO {PoNumber}", poNumber);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { poNumber, error = ex.Message });
        }
    }

    [Function("InspectSalesOrderFromUniconta")]
    public async Task<HttpResponseData> InspectSalesOrderAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "inspect/sales-order/{soNumber:int}")] HttpRequestData req,
        int soNumber,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("InspectSalesOrderFromUniconta started for SO {SoNumber}", soNumber);
        try
        {
            var order = await uniconta.ReadSalesOrderByNumberAsync(soNumber, cancellationToken);
            if (order is null)
                return await WriteJsonAsync(req, HttpStatusCode.NotFound,
                    new { soNumber, message = "No sales order found with that number." });

            var jd = salesOrderMapper.Map(order);
            return await WriteJsonAsync(req, HttpStatusCode.OK, new { soNumber, uniconta = order, jd });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "InspectSalesOrderFromUniconta failed for SO {SoNumber}", soNumber);
            return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new { soNumber, error = ex.Message });
        }
    }

    // Spell out the incoming-shipment fields, including the [JsonIgnore] ones (Sku/unit/SourcePurchaseNumber)
    // that never reach JD's wire payload but are exactly what we need to analyse carrier/kolli/catalog.
    private static object DescribeShipment(JdIncomingShipmentCreate s) => new
    {
        s.text,
        s.carrier,
        s.date,
        s.notificationEmails,
        s.disableApprovalEmail,
        sourcePurchaseNumber = s.SourcePurchaseNumber,
        lines = s.lines.Select(l => new
        {
            l.isSubItem,
            l.quantity,
            sku = l.Sku,
            l.unit,
            l.externalIdentification
        })
    };

    private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object payload)
    {
        var response = req.CreateResponse(status);
        response.Headers.Remove("Content-Type");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
