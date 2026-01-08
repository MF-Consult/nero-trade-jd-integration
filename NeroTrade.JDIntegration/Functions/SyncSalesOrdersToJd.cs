using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;
using NeroTrade.JDIntegration.Services.PdfGeneration;
using System.Net.Http;
using System.Net.Http.Headers;

public sealed class SyncSalesOrdersToJd
{
    private readonly IJdLogisticsService _jd;
    private readonly IUnicontaService _uniconta;
    private readonly ILogger<SyncSalesOrdersToJd> _logger;
    private readonly SalesOrderMapper _mapper;
    private readonly IDeliveryNotePdfService _pdfService;

    private static readonly HttpClient FileUploadHttpClient = new();

    public SyncSalesOrdersToJd(
        IJdLogisticsService jd, 
        IUnicontaService uniconta, 
        SalesOrderMapper mapper,
        IDeliveryNotePdfService pdfService,
        ILogger<SyncSalesOrdersToJd> logger)
    {
        _jd = jd;
        _uniconta = uniconta;
        _mapper = mapper;
        _pdfService = pdfService;
        _logger = logger;
    }

    [Function("SyncSalesOrdersToJd")]
    public async Task RunAsync([HttpTrigger(AuthorizationLevel.Function, "get", Route = "sync-salesorders-to-jd")] HttpRequestData httpReq)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        _logger.LogInformation("SyncSalesOrdersToJd started");

        var inventories = await _jd.GetInventoriesAsync(cts.Token);
        var inventory = inventories.FirstOrDefault();
        if (inventory == null || inventory.id == null)
        {
            _logger.LogWarning("No inventories available in JD");
            return;
        }

        // Build RequestOrder from Sales Orders (DebtorOrderClient) per user's mapping
        var batch = new List<JdRequestOrderCreate>(capacity: 200);

        // Get all delivery notes for debtors to process files
        var deliveryNotesByDebtor = await LoadDeliveryNotesByDebtorAsync(cts.Token);
        var totalDeliveryNotes = deliveryNotesByDebtor.Sum(kvp => kvp.Value.Count);
        _logger.LogInformation("Found {Count} delivery notes for processing", totalDeliveryNotes);

        await foreach (var so in _uniconta.ReadSalesOrdersBatchedAsync(200, cts.Token))
        {
            // Generate and upload PDF delivery note for this order
            var generatedPdfFiles = await GenerateAndUploadDeliveryNotePdfAsync(so, cts.Token);
            
            // Also upload any existing delivery notes from Uniconta (optional)
            var existingDeliveryNotes = await UploadDeliveryNotesAsync(so.DebtorAccount, deliveryNotesByDebtor, cts.Token);
            
            // Combine both file lists (generated PDF + existing files)
            var allFiles = generatedPdfFiles.Concat(existingDeliveryNotes).ToList();

            batch.Add(_mapper.Map(so, allFiles));
            if (batch.Count >= 200)
            {
                await HandleBatchAsync(inventory.id.Value, batch, cts.Token);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await HandleBatchAsync(inventory.id.Value, batch, cts.Token);
            batch.Clear();
        }
    }

    private async Task HandleBatchAsync(long inventoryId, List<JdRequestOrderCreate> batch, CancellationToken ct)
    {
        var result = await _jd.UpsertRequestOrdersAsync(inventoryId, batch, ct);
        if (result.Failures.Count > 0)
        {
            var sample = string.Join(", ", result.Failures.Take(5).Select(f => f.Item.text));
            _logger.LogWarning("JD request orders upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }
        _logger.LogInformation("JD request orders upsert success={Success} failures={Failures}", result.SuccessCount, result.Failures.Count);
    }

    private async Task<Dictionary<string?, List<DebtorDeliveryNoteInfo>>> LoadDeliveryNotesByDebtorAsync(CancellationToken cancellationToken)
    {
        var deliveryNotesByDebtor = new Dictionary<string?, List<DebtorDeliveryNoteInfo>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var note in _uniconta.ReadDebtorDeliveryNotesAsync(cancellationToken))
        {
            if (note.DebtorAccount == null) continue;

            if (!deliveryNotesByDebtor.TryGetValue(note.DebtorAccount, out var list))
            {
                list = new List<DebtorDeliveryNoteInfo>();
                deliveryNotesByDebtor[note.DebtorAccount] = list;
            }

            list.Add(note);
        }

        return deliveryNotesByDebtor;
    }

    /// <summary>
    /// Generates a PDF delivery note for the sales order and uploads it to JD as a package label.
    /// </summary>
    private async Task<IReadOnlyCollection<JdRequestOrderFileRef>> GenerateAndUploadDeliveryNotePdfAsync(
        LocalSalesOrder salesOrder,
        CancellationToken cancellationToken)
    {
        try
        {
            // Generate PDF from sales order
            _logger.LogDebug("Generating PDF delivery note for order {OrderNumber}", salesOrder.OrderNumber);
            var pdfBytes = await _pdfService.GenerateDeliveryNotePdfAsync(salesOrder);
            
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                _logger.LogWarning("PDF generation returned empty bytes for order {OrderNumber}", salesOrder.OrderNumber);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            var displayName = $"Folgeseddel_{salesOrder.OrderNumber}.pdf";
            var description = $"Delivery note for order {salesOrder.OrderNumber}";

            // Step 1: Create file metadata in JD and get pre-signed upload URL
            var (ok, status, message, file, uploadUrl) = await _jd.CreateFileAsync(
                displayName,
                description,
                cancellationToken);

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                _logger.LogWarning("Failed to create JD file for order {Order}. Status={Status} Message={Message}", 
                    salesOrder.OrderNumber, status, message);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 2: Upload PDF content to pre-signed URL
            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, pdfBytes, "application/pdf", cancellationToken);
            if (!uploadSucceeded)
            {
                _logger.LogWarning("Failed to upload PDF content for JD file {FileId} order {Order}", 
                    file.id, salesOrder.OrderNumber);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 3: Verify file was uploaded successfully
            var verification = await _jd.VerifyFileAsync(file.id, cancellationToken);
            if (!verification.ok)
            {
                _logger.LogWarning("Failed to verify JD file {FileId} for order {Order}: {Message}", 
                    file.id, salesOrder.OrderNumber, verification.message);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            _logger.LogInformation("Successfully generated and uploaded PDF delivery note as JD file {FileId} for order {Order}", 
                file.id, salesOrder.OrderNumber);

            // Return file reference with packageLabel = true (important!)
            return new[]
            {
                new JdRequestOrderFileRef
                {
                    id = file.id,
                    packageLabel = true  // Mark as package label
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating or uploading PDF for order {Order}", salesOrder.OrderNumber);
            return Array.Empty<JdRequestOrderFileRef>();
        }
    }

    private async Task<IReadOnlyCollection<JdRequestOrderFileRef>> UploadDeliveryNotesAsync(
        string? debtorAccount,
        IReadOnlyDictionary<string?, List<DebtorDeliveryNoteInfo>> deliveryNotesByDebtor,
        CancellationToken cancellationToken)
    {
        if (debtorAccount == null) return Array.Empty<JdRequestOrderFileRef>();
        if (!deliveryNotesByDebtor.TryGetValue(debtorAccount, out var notes) || notes.Count == 0)
            return Array.Empty<JdRequestOrderFileRef>();

        var files = new List<JdRequestOrderFileRef>(capacity: notes.Count);

        foreach (var note in notes)
        {
            if (note.FileData == null || note.FileData.Length == 0)
            {
                _logger.LogDebug("Skipping delivery note {Note} for debtor {Debtor} due to missing file data", note.NoteNumber, debtorAccount);
                continue;
            }

            var (ok, status, message, file, uploadUrl) = await _jd.CreateFileAsync(
                note.NoteName ?? $"DeliveryNote_{note.NoteNumber}",
                $"Delivery note for debtor {debtorAccount}",
                cancellationToken);

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                _logger.LogWarning("Failed to create JD file for debtor {Debtor}. Status={Status} Message={Message}", debtorAccount, status, message);
                continue;
            }

            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, note.FileData, note.MimeType, cancellationToken);
            if (!uploadSucceeded)
            {
                _logger.LogWarning("Failed to upload content for JD file {FileId} debtor {Debtor}", file.id, debtorAccount);
                continue;
            }

            var verification = await _jd.VerifyFileAsync(file.id, cancellationToken);
            if (!verification.ok)
            {
                _logger.LogWarning("Failed to verify JD file {FileId} for debtor {Debtor}: {Message}", file.id, debtorAccount, verification.message);
                continue;
            }

            files.Add(new JdRequestOrderFileRef
            {
                id = file.id,
                packageLabel = false  // Existing delivery notes are not package labels
            });

            _logger.LogInformation("Uploaded delivery note {Note} as JD file {FileId} for debtor {Debtor}", note.NoteNumber, file.id, debtorAccount);
        }

        return files;
    }

    private static async Task<bool> UploadFileContentAsync(string uploadUrl, byte[] fileData, string? mimeType, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(fileData);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
        content.Headers.ContentLength = fileData.Length;

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        
        using var response = await FileUploadHttpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
