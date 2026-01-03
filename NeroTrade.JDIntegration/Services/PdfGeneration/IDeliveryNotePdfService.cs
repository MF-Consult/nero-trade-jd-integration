namespace NeroTrade.JDIntegration.Services.PdfGeneration;

using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

public interface IDeliveryNotePdfService
{
    /// <summary>
    /// Generates a delivery note (Følgeseddel) PDF for a sales order.
    /// </summary>
    /// <param name="salesOrder">Sales order data</param>
    /// <returns>PDF content as byte array</returns>
    Task<byte[]> GenerateDeliveryNotePdfAsync(LocalSalesOrder salesOrder);
}

