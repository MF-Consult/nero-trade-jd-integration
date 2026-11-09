using System.Net;
using System.Net.Mail;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using NeroTrade.JDIntegration.Services.Alerts;
using NeroTrade.JDIntegration.Services.Logging;

namespace NeroTrade.JDIntegration.Functions.Admin;

/// <summary>
/// <c>POST /api/remediation/test-customer-alert?to=someone@example.com</c> — sends one sample customer error mail
/// through the live Resend setup (key, verified sender, formatter) to the address in <c>to</c>, never to the
/// configured customer. Proves the mail path end to end without touching Uniconta or JD and without mailing the
/// customer. The sample is a real reject reason (PO 98, missing catalog item), prefixed "[TEST]".
///
/// Function-key protected. Returns 409 when customer alerts are not fully configured (the sender is only
/// registered then), so a 200 means production would mail too.
/// </summary>
public sealed class CustomerAlertTestEndpoint(IServiceProvider services)
{
    [Function("TestCustomerAlert")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "remediation/test-customer-alert")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var to = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["to"]?.Trim();
        if (string.IsNullOrEmpty(to) || !MailAddress.TryCreate(to, out _))
            return await RespondAsync(req, HttpStatusCode.BadRequest, new { error = "Query parameter 'to' must be one valid e-mail address." });

        var sender = services.GetService<ICustomerAlertSender>();
        if (sender is null)
            return await RespondAsync(req, HttpStatusCode.Conflict, new { error = "Customer alerts are not fully configured (CustomerAlerts__Enabled/ResendApiKey/From/To)." });

        const string reason = "No matching JD catalog item for SKU(s): DKABNPSCUP200WH. The item must exist in JD's catalog before this purchase order can be received.";
        var sample = new IntegrationLogEntry("test", "error", "JD", "98", reason, null, null)
        {
            ErrorCode = "JD_VALIDATION_REJECTED",
            AlertSubject = CustomerAlertSubject.PurchaseOrder,
            AlertReason = reason
        };
        var alert = CustomerAlertFormatter.TryFormat(sample)!;

        try
        {
            await sender.SendAsync($"[TEST] {alert.Subject}", "Dette er en testmail fra integrationen. Sådan ser en rigtig fejlmail ud:\n\n" + alert.Body, cancellationToken, new[] { to });
        }
        catch (Exception ex)
        {
            return await RespondAsync(req, HttpStatusCode.BadGateway, new { error = "Resend refused the mail.", detail = ex.Message });
        }

        return await RespondAsync(req, HttpStatusCode.OK, new { sent = true, to, subject = $"[TEST] {alert.Subject}" });
    }

    private static async Task<HttpResponseData> RespondAsync(HttpRequestData req, HttpStatusCode status, object body)
    {
        // WriteAsJsonAsync would reset the status to 200, so write the JSON by hand.
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await res.WriteStringAsync(System.Text.Json.JsonSerializer.Serialize(body));
        return res;
    }
}
