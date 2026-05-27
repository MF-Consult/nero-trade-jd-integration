namespace NeroTrade.JDIntegration.Services.PdfGeneration;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using System.Globalization;

/// <summary>
/// Service for generating delivery note (Følgeseddel) PDFs using QuestPDF.
/// 
/// Customization points:
/// - ComposeHeader(): Company logo and contact info
/// - ComposeContent(): Main document layout (customer info, order details, products)
/// - ComposeFooter(): Footer with company details
/// 
/// Colors:
/// - Company name: #1E5B8C (blue)
/// - Table header: #E0E0E0 (light gray)
/// - Table borders: #CCCCCC (gray)
/// 
/// Test this service using: /api/test-generate-pdf
/// </summary>
public class DeliveryNotePdfService : IDeliveryNotePdfService
{
    static DeliveryNotePdfService()
    {
        // Configure QuestPDF license (Community license is free for non-commercial/small commercial use)
        // See: https://www.questpdf.com/license/
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<byte[]> GenerateDeliveryNotePdfAsync(LocalSalesOrder salesOrder)
    {
        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.MarginTop(15);
                page.MarginBottom(15);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Segoe UI"));

                page.Header().Element(ComposeHeader);
                page.Content().Element(content => ComposeContent(content, salesOrder));
                page.Footer().Element(ComposeFooter);
            });
        }).GeneratePdf();

        return Task.FromResult(pdfBytes);
    }

    private void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            // Company Logo (left side)
            row.RelativeItem().Column(column =>
            {
                var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "nero-trade-logo.png");
                if (File.Exists(logoPath))
                {
                    column.Item().Height(40).Image(logoPath);
                }
                else
                {
                    // Fallback to text if logo not found
                    column.Item().Text("Nero Trade")
                        .FontSize(28)
                        .Bold()
                        .FontColor("#0066A1")
                        .FontFamily("Segoe UI");
                }
            });

            // Company Info (right side)
            row.RelativeItem().Column(column =>
            {
                column.Item().AlignRight().Text("Nero Trade ApS").FontSize(9);
                column.Item().AlignRight().Text("+45 42429620").FontSize(9);
                column.Item().AlignRight().Text("mb@nerotrade.dk").FontSize(9);
                column.Item().AlignRight().Text("www.nerotrade.dk").FontSize(9);
                column.Item().AlignRight().Text("CVR.: 41623799").FontSize(9);
            });
        });
    }

    private void ComposeContent(IContainer container, LocalSalesOrder salesOrder)
    {
        container.Column(column =>
        {
            column.Spacing(10);

            // Title
            column.Item().Text("FØLGESEDDEL")
                .FontSize(16)
                .Bold();

            // Customer Information Box with rounded corners
            column.Item()
                .Border(1)
                .BorderColor("#CCCCCC")
                .CornerRadius(5)
                .Padding(6)
                .Column(innerColumn =>
                {
                    innerColumn.Item().Text("Kunde").Bold();
                    innerColumn.Item().PaddingTop(3).Text(salesOrder.DebtorName ?? "");
                    innerColumn.Item().PaddingTop(2).Text(salesOrder.DeliveryAddress1 ?? "");
                    if (!string.IsNullOrWhiteSpace(salesOrder.DeliveryAddress2))
                        innerColumn.Item().PaddingTop(2).Text(salesOrder.DeliveryAddress2);
                    innerColumn.Item().PaddingTop(2).Text($"{salesOrder.DeliveryZip ?? ""} {salesOrder.DeliveryCity ?? ""}");
                    innerColumn.Item().PaddingTop(2).Text(salesOrder.DeliveryCountryCode ?? "");
                    
                    if (!string.IsNullOrWhiteSpace(salesOrder.DebtorCVR))
                        innerColumn.Item().PaddingTop(2).Text($"CVR-nr: {salesOrder.DebtorCVR}");
                });

            // Order Details and Delivery Address Row with rounded corners
            column.Item().Row(row =>
            {
                // Order Details (left)
                row.RelativeItem()
                    .PaddingRight(10)
                    .Border(1)
                    .BorderColor("#CCCCCC")
                    .CornerRadius(5)
                    .Padding(6)
                    .Column(detailsColumn =>
                    {
                        detailsColumn.Item().Row(r =>
                        {
                            r.AutoItem().Width(120).Text("Ordrenummer").Bold();
                            r.RelativeItem().Text(salesOrder.OrderNumber.ToString());
                        });
                        
                        detailsColumn.Item().PaddingTop(2).Row(r =>
                        {
                            r.AutoItem().Width(120).Text("Dato").Bold();
                            r.RelativeItem().Text(salesOrder.Date?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "");
                        });
                        
                        detailsColumn.Item().PaddingTop(2).Row(r =>
                        {
                            r.AutoItem().Width(120).Text("Leveringsdato").Bold();
                            r.RelativeItem().Text(salesOrder.DeliveryDate?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture) ?? "");
                        });
                        
                        detailsColumn.Item().PaddingTop(2).Row(r =>
                        {
                            r.AutoItem().Width(120).Text("Rekvisitionsnr.").Bold();
                            r.RelativeItem().Text(salesOrder.YourReference ?? "");
                        });
                        
                        detailsColumn.Item().PaddingTop(2).Row(r =>
                        {
                            r.AutoItem().Width(120).Text("Vores ref").Bold();
                            r.RelativeItem().Text(salesOrder.OurReference ?? "");
                        });
                    });

                // Delivery Address (right)
                row.RelativeItem()
                    .Border(1)
                    .BorderColor("#CCCCCC")
                    .CornerRadius(8)
                    .Padding(6)
                    .Column(addressColumn =>
                    {
                        addressColumn.Item().Text("Leveringsadresse").Bold();
                        addressColumn.Item().PaddingTop(3).Text(salesOrder.DeliveryName ?? "");
                        addressColumn.Item().Text(salesOrder.DeliveryAddress1 ?? "");
                        if (!string.IsNullOrWhiteSpace(salesOrder.DeliveryAddress2))
                            addressColumn.Item().Text(salesOrder.DeliveryAddress2);
                        addressColumn.Item().Text($"{salesOrder.DeliveryZip ?? ""} {salesOrder.DeliveryCity ?? ""}");
                    });
            });

            // Comments (if any) with rounded corners
            if (!string.IsNullOrWhiteSpace(salesOrder.Comments))
            {
                column.Item()
                    .Border(1)
                    .BorderColor("#CCCCCC")
                    .CornerRadius(5)
                    .Padding(6)
                    .Column(commentColumn =>
                    {
                        commentColumn.Item().Text("Kommentar").Bold();
                        commentColumn.Item().PaddingTop(3).Text(salesOrder.DeliveryNoteText ?? "");
                    });
            }

            // Product Table with rounded border
            column.Item()
                .Border(1)
                .BorderColor("#CCCCCC")
                .CornerRadius(5)
                .Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(5); // Vare (SKU)
                        columns.RelativeColumn(8); // Tekst (Description)
                        columns.RelativeColumn(2); // Antal (Quantity)
                        columns.RelativeColumn(2); // Enhed (Unit)
                        columns.RelativeColumn(2); // Leveret (Delivered)
                    });

                    // Header with background
                    table.Header(header =>
                    {
                        header.Cell().Background("#F5F5F5").BorderBottom(1).BorderColor("#CCCCCC").Padding(4).PaddingLeft(2).Text("Vare").Bold();
                        header.Cell().Background("#F5F5F5").BorderBottom(1).BorderColor("#CCCCCC").Padding(4).Text("Tekst").Bold();
                        header.Cell().Background("#F5F5F5").BorderBottom(1).BorderColor("#CCCCCC").Padding(4).AlignRight().Text("Antal").Bold();
                        header.Cell().Background("#F5F5F5").BorderBottom(1).BorderColor("#CCCCCC").Padding(4).Text("Enhed").Bold();
                        header.Cell().Background("#F5F5F5").BorderBottom(1).BorderColor("#CCCCCC").Padding(4).PaddingRight(2).AlignRight().Text("Leveret").Bold();
                    });

                    // Rows with spacing
                    foreach (var line in salesOrder.Lines)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor("#E0E0E0").Padding(4).PaddingLeft(2).Text(line.Sku ?? "");
                        table.Cell().BorderBottom(0.5f).BorderColor("#E0E0E0").Padding(4).Text(line.ItemName ?? "");
                        table.Cell().BorderBottom(0.5f).BorderColor("#E0E0E0").Padding(4).AlignRight()
                            .Text(line.Quantity.ToString("N0", CultureInfo.InvariantCulture));
                        table.Cell().BorderBottom(0.5f).BorderColor("#E0E0E0").Padding(4).Text(line.Unit ?? "stk");
                        table.Cell().BorderBottom(0.5f).BorderColor("#E0E0E0").Padding(4).PaddingRight(2).AlignRight()
                            .Text(line.Quantity.ToString("N0", CultureInfo.InvariantCulture));
                    }
                });
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.AlignCenter()
        .Border(1)
        .BorderColor("#CCCCCC")
        .CornerRadius(5)
        .Padding(3)
        .PaddingLeft(10)
        .PaddingRight(10)
        .Text(text =>
        {
            text.Span("Nero Trade ApS | ").FontSize(7);
            text.Span("Lyngbyvej 83A | ").FontSize(7);
            text.Span("2100 København Ø | ").FontSize(7);
            text.Span("Denmark | ").FontSize(7);
            text.Span("Tlf.: +45 42429620 | ").FontSize(7);
            text.Span("E-mail: mb@nerotrade.dk | ").FontSize(7);
            text.Span("www.nerotrade.dk | ").FontSize(7);
            text.Span("CVR-nr.: 41623799").FontSize(7);
        });
    }
}

