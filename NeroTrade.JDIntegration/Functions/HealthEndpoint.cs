using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.Scheduling;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// <c>GET /api/inspect/health</c> — per-job last start, configured cadence and a stale flag, plus when the last JD
/// webhook arrived. Read-only and in-memory (no Uniconta/JD calls), so an external monitor (Hermes) can poll it
/// every few minutes. It reports this process only: after a restart the timestamps are empty until each job has
/// run once, so a monitor should treat <c>lastStartUtc == null</c> as "unknown", not "stale".
/// </summary>
public sealed class HealthEndpoint(SyncScheduler scheduler, JdWebhookOptions webhookOptions)
{
    [Function("InspectHealth")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "inspect/health")] HttpRequestData req)
    {
        var now = DateTime.UtcNow;
        var jobs = scheduler.Snapshot(now);
        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(new
        {
            nowUtc = now,
            webhookConfigured = !string.IsNullOrEmpty(webhookOptions.Secret),
            anyStale = jobs.Any(j => j.Stale),
            jobs
        });
        return res;
    }
}
