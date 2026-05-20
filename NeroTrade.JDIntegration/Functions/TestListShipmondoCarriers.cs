using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Diagnostic endpoint to discover Shipmondo carrier slugs and products as JD has them configured.
/// JD requires ReceiverCountryCode + zip to scope the result; defaults: country=DK, zip=5220.
/// Access (all hits are scoped to <c>api/shipmondo/</c> — arbitrary path passthrough is intentionally not supported):
///   /api/test-shipmondo-carriers                                  → carriers for DK 5220
///   /api/test-shipmondo-carriers?country=DK&amp;zip=5220              → carriers for given country/zip
///   /api/test-shipmondo-carriers?carrier=egs&amp;country=DK&amp;zip=5220  → products for carrier in that zone
/// </summary>
public sealed class TestListShipmondoCarriers
{
    private static readonly Regex CarrierSlug = new("^[a-zA-Z0-9_-]{1,40}$", RegexOptions.Compiled);

    private readonly IJdRepository _jd;
    private readonly ILogger<TestListShipmondoCarriers> _logger;

    public TestListShipmondoCarriers(IJdRepository jd, ILogger<TestListShipmondoCarriers> logger)
    {
        _jd = jd;
        _logger = logger;
    }

    [Function("TestListShipmondoCarriers")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "test-shipmondo-carriers")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var carrier = query["carrier"];
        var country = string.IsNullOrWhiteSpace(query["country"]) ? "DK" : query["country"]!;
        var zip = string.IsNullOrWhiteSpace(query["zip"]) ? "5220" : query["zip"]!;

        if (!string.IsNullOrWhiteSpace(carrier) && !CarrierSlug.IsMatch(carrier))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid 'carrier' value — must match [a-zA-Z0-9_-]{1,40}.", cancellationToken);
            return bad;
        }

        var zoneQs = $"ReceiverCountryCode={Uri.EscapeDataString(country)}&ReceiverZipCode={Uri.EscapeDataString(zip)}";

        string relativePath = string.IsNullOrWhiteSpace(carrier)
            ? $"api/shipmondo/carriers?{zoneQs}"
            : $"api/shipmondo/carriers/{carrier}/products?{zoneQs}";

        _logger.LogInformation("TestListShipmondoCarriers calling JD: {Path}", relativePath);

        var (status, body) = await _jd.GetRawAsync(relativePath, cancellationToken);

        var response = req.CreateResponse(status >= 200 && status < 300 ? HttpStatusCode.OK : (HttpStatusCode)status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(body, cancellationToken);
        return response;
    }
}
