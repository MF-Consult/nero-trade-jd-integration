using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using NeroTrade.JDIntegration.Models.ExternalIntegration;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.PdfGeneration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Constants;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Models;

namespace NeroTrade.JDIntegration.Functions;

public sealed class SyncSalesOrdersToJd(
    IJdLogisticsService jd,
    IUnicontaService uniconta,
    SalesOrderMapper mapper,
    IDeliveryNotePdfService pdfService,
    IIntegrationLogger integrationLogger,
    SupabaseOptions supabaseOptions,
    ILogger<SyncSalesOrdersToJd> logger)
{
    private static readonly HttpClient FileUploadHttpClient = new();

    // Prevents overlapping runs within the same worker instance. A previous run that lags past
    // the 30s trigger interval would otherwise race the next run on Uniconta state — most visibly
    // by overwriting a manual flueben re-check on a Fejlet order with stale snapshot data.
    // Skip (not queue) when the lock is held: queuing just amplifies the same race.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    [Function("SyncSalesOrdersToJd")]
    public async Task RunAsync([TimerTrigger("*/30 * * * * *")] TimerInfo timer)
    {
        if (!await RunLock.WaitAsync(0))
        {
            logger.LogInformation("SyncSalesOrdersToJd skipped — previous run still in progress");
            return;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
        logger.LogInformation("SyncSalesOrdersToJd started");

        try
        {
            var inventories = await jd.GetInventoriesAsync(cts.Token);
            var inventory = inventories.FirstOrDefault();
            if (inventory == null || inventory.id == null)
            {
                logger.LogWarning("No inventories available in JD");
                return;
            }

            // Build RequestOrder from Sales Orders (DebtorOrderClient) per user's mapping
            var batch = new List<JdRequestOrderCreate>(capacity: 200);

            // Existing Uniconta delivery notes are not yet wired up (see UnicontaRepository.ReadDebtorDeliveryNotesAsync).
            // TODO: Populate this once ReadDebtorDeliveryNotesAsync is implemented.
            var deliveryNotesByDebtor = new Dictionary<string?, List<DebtorDeliveryNoteInfo>>(StringComparer.OrdinalIgnoreCase);

            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;

            await foreach (var so in uniconta.ReadSalesOrdersBatchedAsync(200, cts.Token))
            {
                // Generate and upload PDF delivery note for this order
                var generatedPdfFiles = await GenerateAndUploadDeliveryNotePdfAsync(so, cts.Token);

                // Also upload any existing delivery notes from Uniconta (optional)
                var existingDeliveryNotes = await UploadDeliveryNotesAsync(so.DebtorAccount, deliveryNotesByDebtor, cts.Token);

                // Combine both file lists (generated PDF + existing files)
                var allFiles = generatedPdfFiles.Concat(existingDeliveryNotes).ToList();

                batch.Add(mapper.Map(so, allFiles));
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(inventory.id.Value, batch, cts.Token);
                    totalProcessed += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(inventory.id.Value, batch, cts.Token);
                totalProcessed += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            if (totalProcessed > 0)
            {
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", null,
                    $"SyncSalesOrdersToJd completed: {totalSucceeded} succeeded, {totalFailed} failed.",
                    null,
                    JsonSerializer.SerializeToElement(new { processed = totalProcessed, succeeded = totalSucceeded, failed = totalFailed })
                ), cts.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncSalesOrdersToJd failed");
            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "Integration", null,
                $"SyncSalesOrdersToJd run failed: {ex.Message}", ex.ToString(), null
            ), CancellationToken.None);
            throw;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(long inventoryId, List<JdRequestOrderCreate> batch, CancellationToken ct)
    {
        var result = await jd.UpsertRequestOrdersAsync(inventoryId, batch, ct);

        var failedOrderNumbers = result.Failures
            .Where(f => f.Item.SourceOrderNumber.HasValue)
            .Select(f => f.Item.SourceOrderNumber!.Value)
            .ToHashSet();

        // Lock successfully-handled orders so they are not re-processed (re-PDF'd) next run, and clear any
        // stale error message. SyncRequestOrderStatusToUniconta later replaces "Oprettet" with the live JD status.
        int markedCreated = 0;
        foreach (var order in batch)
        {
            if (!order.SourceOrderNumber.HasValue || failedOrderNumbers.Contains(order.SourceOrderNumber.Value)) continue;
            var success = await uniconta.SetSalesOrderStatusAsync(order.SourceOrderNumber.Value, SalesOrderJdGroup.Created, new Dictionary<string, object>
            {
                [UnicontaUserFields.IntegrationIssue] = string.Empty,
            }, ct);
            if (success)
            {
                markedCreated++;
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "info", "Integration", order.SourceOrderNumber.Value.ToString(),
                    $"Sales order {order.SourceOrderNumber} synced to JD.", null, null), ct);
            }
            else
            {
                logger.LogError("Failed to set SO {Order} group to Oprettet", order.SourceOrderNumber.Value);
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    supabaseOptions.IntegrationName, "warning", "Uniconta", order.SourceOrderNumber.Value.ToString(),
                    $"Sales order {order.SourceOrderNumber} was sent to JD but the Uniconta status update failed; will retry next run.",
                    null, null), ct);
            }
        }

        // Failed in JD: status = Fejlet, record why, and consume the transfer trigger. It will only be
        // retried once a user fixes the issue and sets Xoverfor1 again (Group is then "Fejlet" or empty).
        int markedFailed = 0;
        foreach (var failure in result.Failures)
        {
            if (!failure.Item.SourceOrderNumber.HasValue) continue;
            var success = await uniconta.SetSalesOrderStatusAsync(failure.Item.SourceOrderNumber.Value, SalesOrderJdGroup.Failed, new Dictionary<string, object>
            {
                [UnicontaUserFields.IntegrationIssue] = string.IsNullOrWhiteSpace(failure.Message) ? "Ukendt fejl ved oprettelse i JD" : failure.Message,
                [UnicontaUserFields.SalesOrderTransferFlag] = false,
            }, ct);
            if (success) markedFailed++;
            else logger.LogError("Failed to set SO {Order} group to Fejlet", failure.Item.SourceOrderNumber.Value);

            await integrationLogger.LogAsync(new IntegrationLogEntry(
                supabaseOptions.IntegrationName, "error", "JD", failure.Item.SourceOrderNumber.Value.ToString(),
                $"JD rejected sales order {failure.Item.SourceOrderNumber}: {failure.Message}", null,
                JsonSerializer.SerializeToElement(new { errorMessage = failure.Message, sourceOrderNumber = failure.Item.SourceOrderNumber })), ct);
        }

        logger.LogInformation("JD request orders: success={Success} marked_oprettet={MarkedCreated} failures={Failures} marked_fejlet={MarkedFailed}",
            result.SuccessCount, markedCreated, result.Failures.Count, markedFailed);

        if (result.Failures.Count > 0)
        {
            var sample = string.Join("; ", result.Failures.Take(5).Select(f => $"{f.Item.text}: {f.Message}"));
            logger.LogWarning("JD request orders upsert failures: {Count}. Sample: {Sample}", result.Failures.Count, sample);
        }

        return (batch.Count, result.SuccessCount, result.Failures.Count);
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
            logger.LogDebug("Generating PDF delivery note for order {OrderNumber}", salesOrder.OrderNumber);
            var pdfBytes = await pdfService.GenerateDeliveryNotePdfAsync(salesOrder);
            
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                logger.LogWarning("PDF generation returned empty bytes for order {OrderNumber}", salesOrder.OrderNumber);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            var displayName = $"Folgeseddel_{salesOrder.OrderNumber}.pdf";
            var description = $"Delivery note for order {salesOrder.OrderNumber}";

            // Step 1: Create file metadata in JD and get pre-signed upload URL
            var (ok, status, message, file, uploadUrl) = await jd.CreateFileAsync(
                displayName,
                description,
                cancellationToken);

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                logger.LogWarning("Failed to create JD file for order {Order}. Status={Status} Message={Message}", 
                    salesOrder.OrderNumber, status, message);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 2: Upload PDF content to pre-signed URL
            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, pdfBytes, "application/pdf", cancellationToken);
            if (!uploadSucceeded)
            {
                logger.LogWarning("Failed to upload PDF content for JD file {FileId} order {Order}", 
                    file.id, salesOrder.OrderNumber);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 3: Verify file was uploaded successfully
            var verification = await jd.VerifyFileAsync(file.id, cancellationToken);
            if (!verification.ok)
            {
                logger.LogWarning("Failed to verify JD file {FileId} for order {Order}: {Message}", 
                    file.id, salesOrder.OrderNumber, verification.message);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            logger.LogInformation("Successfully generated and uploaded PDF delivery note as JD file {FileId} for order {Order}", 
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
            logger.LogError(ex, "Error generating or uploading PDF for order {Order}", salesOrder.OrderNumber);
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
                logger.LogDebug("Skipping delivery note {Note} for debtor {Debtor} due to missing file data", note.NoteNumber, debtorAccount);
                continue;
            }

            var (ok, status, message, file, uploadUrl) = await jd.CreateFileAsync(
                note.NoteName ?? $"DeliveryNote_{note.NoteNumber}",
                $"Delivery note for debtor {debtorAccount}",
                cancellationToken);

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                logger.LogWarning("Failed to create JD file for debtor {Debtor}. Status={Status} Message={Message}", debtorAccount, status, message);
                continue;
            }

            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, note.FileData, note.MimeType, cancellationToken);
            if (!uploadSucceeded)
            {
                logger.LogWarning("Failed to upload content for JD file {FileId} debtor {Debtor}", file.id, debtorAccount);
                continue;
            }

            var verification = await jd.VerifyFileAsync(file.id, cancellationToken);
            if (!verification.ok)
            {
                logger.LogWarning("Failed to verify JD file {FileId} for debtor {Debtor}: {Message}", file.id, debtorAccount, verification.message);
                continue;
            }

            files.Add(new JdRequestOrderFileRef
            {
                id = file.id,
                packageLabel = false  // Existing delivery notes are not package labels
            });

            logger.LogInformation("Uploaded delivery note {Note} as JD file {FileId} for debtor {Debtor}", note.NoteNumber, file.id, debtorAccount);
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