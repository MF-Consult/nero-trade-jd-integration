using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Diagnostic endpoint to discover Shipmondo carrier slugs and products as JD has them configured.
/// JD requires ReceiverCountryCode + zip to scope the result; defaults: country=DK, zip=5220.
/// Access:
///   /api/test-shipmondo-carriers                                  → carriers for DK 5220
///   /api/test-shipmondo-carriers?country=DK&zip=5220              → carriers for given country/zip
///   /api/test-shipmondo-carriers?carrier=egs&country=DK&zip=5220  → products for carrier in that zone
///   /api/test-shipmondo-carriers?path=any/raw/path                → raw GET pass-through (debug)
/// </summary>
public sealed class TestListShipmondoCarriers
{
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
        var rawPath = query["path"];
        var country = string.IsNullOrWhiteSpace(query["country"]) ? "DK" : query["country"]!;
        var zip = string.IsNullOrWhiteSpace(query["zip"]) ? "5220" : query["zip"]!;

        var zoneQs = $"ReceiverCountryCode={Uri.EscapeDataString(country)}&ReceiverZipCode={Uri.EscapeDataString(zip)}";

        string relativePath;
        if (!string.IsNullOrWhiteSpace(rawPath))
            relativePath = rawPath;
        else if (!string.IsNullOrWhiteSpace(carrier))
            relativePath = $"api/shipmondo/carriers/{carrier}/products?{zoneQs}";
        else
            relativePath = $"api/shipmondo/carriers?{zoneQs}";

        _logger.LogInformation("TestListShipmondoCarriers calling JD: {Path}", relativePath);

        var (status, body) = await _jd.GetRawAsync(relativePath, cancellationToken);

        var response = req.CreateResponse(status >= 200 && status < 300 ? HttpStatusCode.OK : (HttpStatusCode)status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(body, cancellationToken);
        return response;
    }
}
