using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.PdfGeneration;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using System.Net;

namespace NeroTrade.JDIntegration.Functions;

/// <summary>
/// Simple test function to generate a sample delivery note PDF.
/// Access via: /api/test-generate-pdf
/// </summary>
public sealed class TestGeneratePdf
{
    private readonly IDeliveryNotePdfService _pdfService;
    private readonly ILogger<TestGeneratePdf> _logger;

    public TestGeneratePdf(IDeliveryNotePdfService pdfService, ILogger<TestGeneratePdf> logger)
    {
        _pdfService = pdfService;
        _logger = logger;
    }

    [Function("TestGeneratePdf")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "test-generate-pdf")] HttpRequestData req)
    {
        _logger.LogInformation("TestGeneratePdf function triggered");

        try
        {
            // Create sample sales order matching the PDF template
            var sampleOrder = new LocalSalesOrder
            {
                OrderNumber = 1472,
                Date = DateTime.Parse("2025-09-22"),
                DeliveryDate = DateTime.Now.AddDays(7),
                
                // Customer information
                DebtorAccount = "JD001",
                DebtorName = "JD Logistik A/S",
                DebtorCVR = "28839537",
                
                // Delivery address
                DeliveryName = "Mark",
                DeliveryAddress1 = "Jernholmen 54",
                DeliveryAddress2 = "",
                DeliveryZip = "2650",
                DeliveryCity = "Hvidovre",
                DeliveryCountryCode = "Denmark",
                
                // References
                YourReference = "Rekv",
                OurReference = "Vores Ref",
                
                // Comments
                Comments = "Dette er en Kommentar",
                
                // Order lines
                Lines = new List<LocalSalesOrderLine>
                {
                    new()
                    {
                        Sku = "CHNAIPAPRDHAA900DCPE",
                        ItemName = "Dhaba - 900 Bowl",
                        Quantity = 2,
                        Unit = "stk",
                        Price = 2.00m
                    },
                    new()
                    {
                        Sku = "SEZIPSAU90LIDPET62",
                        ItemName = "Sauce Cup Lid - 90ml - PET",
                        Quantity = 1,
                        Unit = "kolli",
                        Price = 1.00m
                    },
                    new()
                    {
                        Sku = "SEZIPSAU90LIDPET62",
                        ItemName = "Sauce Cup Lid - 90ml - PET",
                        Quantity = 1,
                        Unit = "kolli",
                        Price = 1.00m
                    },
                    new()
                    {
                        Sku = "SEZIPSAU90LIDPET62",
                        ItemName = "Sauce Cup Lid - 90ml - PET",
                        Quantity = 1,
                        Unit = "kolli",
                        Price = 1.00m
                    },
                    new()
                    {
                        Sku = "SEZIPSAU90LIDPET62",
                        ItemName = "Sauce Cup Lid - 90ml - PET",
                        Quantity = 1,
                        Unit = "kolli",
                        Price = 1.00m
                    }
                }
            };

            // Generate PDF
            _logger.LogInformation("Generating PDF for order {OrderNumber}", sampleOrder.OrderNumber);
            var pdfBytes = await _pdfService.GenerateDeliveryNotePdfAsync(sampleOrder);

            _logger.LogInformation("PDF generated successfully. Size: {Size} bytes", pdfBytes.Length);

            // Return PDF as download
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/pdf");
            
            // Use ASCII-safe filename with RFC 5987 encoding for international characters
            var filename = $"Folgeseddel_{sampleOrder.OrderNumber}.pdf";
            var encodedFilename = $"Følgeseddel_{sampleOrder.OrderNumber}.pdf";
            response.Headers.Add("Content-Disposition", 
                $"attachment; filename=\"{filename}\"; filename*=UTF-8''{Uri.EscapeDataString(encodedFilename)}");
            
            await response.Body.WriteAsync(pdfBytes);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating PDF");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }
}

