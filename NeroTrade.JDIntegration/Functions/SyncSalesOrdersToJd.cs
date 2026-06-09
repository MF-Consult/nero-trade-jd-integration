using System.Diagnostics;
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
    ILogger<SyncSalesOrdersToJd> logger)
{
    private static readonly HttpClient FileUploadHttpClient = new() { Timeout = TimeSpan.FromMinutes(2) };

    // Prevents overlapping runs within the same worker instance. A previous run that lags past
    // the 30s trigger interval would otherwise race the next run on Uniconta state — most visibly
    // by overwriting a manual flueben re-check on a Fejlet order with stale snapshot data.
    // Skip (not queue) when the lock is held: queuing just amplifies the same race.
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    [Function("SyncSalesOrdersToJd")]
    public async Task RunAsync([TimerTrigger("0 */1 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        if (!await RunLock.WaitAsync(0, cancellationToken))
        {
            logger.LogInformation("SyncSalesOrdersToJd skipped — previous run still in progress");
            return;
        }

        await using var run = integrationLogger.BeginRun("SyncSalesOrdersToJd");
        var logScope = run.Scope;
        var timings = new RunTimings();
        var runStopwatch = Stopwatch.StartNew();
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = logScope.CorrelationId });
        logger.LogInformation("SyncSalesOrdersToJd started");

        try
        {
            var inventories = await jd.GetInventoriesAsync(cancellationToken);
            var inventory = inventories.FirstOrDefault();
            if (inventory == null || inventory.id == null)
            {
                logger.LogWarning("No inventories available in JD");
                run.ExitReason = "inventories_unavailable";
                // Surface to integration_logs so a JD-returns-empty state is visible without grepping
                // App Insights. With the JdReadCache fix, this should only fire on a cold start (no
                // stale value cached) or if JD legitimately returns zero inventories.
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "warning", "JD", null,
                    "No inventories available in JD — sales-order sync skipped this tick.", null, null)
                {
                    CorrelationId = logScope.CorrelationId,
                    ErrorCode = "JD_LOOKUP_MISS",
                    Retryable = true,
                    SuggestedAction = "Verify JD inventories endpoint is returning data; if no recent JD_LOOKUP_FAILED rows, JD legitimately has zero inventories configured."
                }, cancellationToken);
                return;
            }

            // Build RequestOrder from Sales Orders (DebtorOrderClient) per user's mapping
            var batch = new List<JdRequestOrderCreate>(capacity: 200);

            // Existing Uniconta delivery notes are not yet wired up (see UnicontaRepository.ReadDebtorDeliveryNotesAsync).
            // TODO: Populate this once ReadDebtorDeliveryNotesAsync is implemented.
            var deliveryNotesByDebtor = new Dictionary<string, List<DebtorDeliveryNoteInfo>>(StringComparer.OrdinalIgnoreCase);

            int totalProcessed = 0, totalSucceeded = 0, totalFailed = 0;

            // Iterate manually so we can isolate the time spent waiting on Uniconta from the per-order
            // work (PDF + JD upload). MoveNextAsync covers both the initial bulk queries and any
            // subsequent batched yields from the repository.
            await using var unicontaEnumerator = uniconta.ReadSalesOrdersBatchedAsync(200, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var readSw = new Stopwatch();
            while (true)
            {
                readSw.Restart();
                bool hasNext;
                try { hasNext = await unicontaEnumerator.MoveNextAsync(); }
                finally { readSw.Stop(); timings.UnicontaReadMs += readSw.ElapsedMilliseconds; }
                if (!hasNext) break;

                var so = unicontaEnumerator.Current;
                timings.OrdersRead++;

                // Generate and upload PDF delivery note for this order
                var generatedPdfFiles = await GenerateAndUploadDeliveryNotePdfAsync(so, logScope, timings, cancellationToken);

                // Also upload any existing delivery notes from Uniconta (optional)
                var existingDeliveryNotes = await UploadDeliveryNotesAsync(so.DebtorAccount, deliveryNotesByDebtor, timings, cancellationToken);

                // Combine both file lists (generated PDF + existing files)
                var allFiles = generatedPdfFiles.Concat(existingDeliveryNotes).ToList();

                batch.Add(mapper.Map(so, allFiles));
                if (batch.Count >= 200)
                {
                    var (p, s, f) = await HandleBatchAsync(inventory.id.Value, batch, logScope, timings, cancellationToken);
                    totalProcessed += p; totalSucceeded += s; totalFailed += f;
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                var (p, s, f) = await HandleBatchAsync(inventory.id.Value, batch, logScope, timings, cancellationToken);
                totalProcessed += p; totalSucceeded += s; totalFailed += f;
                batch.Clear();
            }

            runStopwatch.Stop();
            timings.TotalRunMs = runStopwatch.ElapsedMilliseconds;

            // Tag the exit reason so future dead-window analysis can grep payload.exit_reason
            // directly instead of guessing from duration. "no_eligible_orders" covers the common
            // case where Uniconta returned zero matches — the gate was open, the read fired, the
            // filter just had nothing to do. Distinct from "inventories_unavailable" above which
            // never reached the Uniconta read.
            run.ExitReason = timings.OrdersRead > 0 ? "completed" : "no_eligible_orders";

            // Always log timings when we touched at least one order — the goal is to identify
            // which phase dominates the tick latency Maiwand sees. Skip when zero pending orders
            // to avoid noisy "all zeros" rows every 30 seconds.
            if (totalProcessed > 0 || timings.OrdersRead > 0)
            {
                logger.LogInformation(
                    "SyncSalesOrdersToJd diagnostics: total={Total}ms uniconta_read={Read}ms pdf_gen={Pdf}ms jd_create_file={Create}ms blob_upload={Upload}ms jd_verify={Verify}ms jd_upsert={Upsert}ms uniconta_status={Status}ms orders_read={Orders} pdf_ok={PdfOk} pdf_fail={PdfFail}",
                    timings.TotalRunMs, timings.UnicontaReadMs, timings.PdfGenMs, timings.JdCreateFileMs,
                    timings.BlobUploadMs, timings.JdVerifyFileMs, timings.JdUpsertMs, timings.UnicontaStatusUpdateMs,
                    timings.OrdersRead, timings.PdfsSucceeded, timings.PdfsFailed);

                // Single completion row — IntegrationRun.DisposeAsync writes it with this payload
                // wrapped under `counts`, plus run_name/started_at/finished_at/duration_ms.
                run.AttachCompletionPayload(new
                {
                    processed = totalProcessed,
                    succeeded = totalSucceeded,
                    failed = totalFailed,
                    timings_ms = new
                    {
                        total = timings.TotalRunMs,
                        uniconta_read = timings.UnicontaReadMs,
                        pdf_gen = timings.PdfGenMs,
                        jd_create_file = timings.JdCreateFileMs,
                        blob_upload = timings.BlobUploadMs,
                        jd_verify_file = timings.JdVerifyFileMs,
                        jd_upsert = timings.JdUpsertMs,
                        uniconta_status_update = timings.UnicontaStatusUpdateMs
                    },
                    counts = new
                    {
                        orders_read = timings.OrdersRead,
                        pdfs_succeeded = timings.PdfsSucceeded,
                        pdfs_failed = timings.PdfsFailed,
                        jd_batches = timings.JdBatchCount
                    }
                });
                }
            }
            catch (Exception ex)
            {
                run.MarkFailed(ex);
                logger.LogError(ex, "SyncSalesOrdersToJd failed");
                throw;
            }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<(int processed, int succeeded, int failed)> HandleBatchAsync(long inventoryId, List<JdRequestOrderCreate> batch, IntegrationLogScope logScope, RunTimings timings, CancellationToken ct)
    {
        var upsertSw = Stopwatch.StartNew();
        var result = await jd.UpsertRequestOrdersAsync(inventoryId, batch, ct);
        upsertSw.Stop();
        timings.JdUpsertMs += upsertSw.ElapsedMilliseconds;
        timings.JdBatchCount++;

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
            var statusSw = Stopwatch.StartNew();
            var success = await uniconta.SetSalesOrderStatusAsync(order.SourceOrderNumber.Value, SalesOrderJdGroup.Created, new Dictionary<string, object>
            {
                [UnicontaUserFields.IntegrationIssue] = string.Empty,
            }, ct);
            statusSw.Stop();
            timings.UnicontaStatusUpdateMs += statusSw.ElapsedMilliseconds;
            if (success)
            {
                markedCreated++;
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "info", "Integration", order.SourceOrderNumber.Value.ToString(),
                    $"Sales order {order.SourceOrderNumber} synced to JD.", null, null)
                {
                    CorrelationId = logScope.CorrelationId
                }, ct);
                await integrationLogger.MarkResolvedAsync(
                    integrationLogger.IntegrationName,
                    order.SourceOrderNumber.Value.ToString(),
                    logScope.CorrelationId,
                    ct);
            }
            else
            {
                logger.LogError("Failed to set SO {Order} group to Oprettet", order.SourceOrderNumber.Value);
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "warning", "Uniconta", order.SourceOrderNumber.Value.ToString(),
                    $"Sales order {order.SourceOrderNumber} was sent to JD but the Uniconta status update failed; will retry next run.",
                    null, null)
                {
                    CorrelationId = logScope.CorrelationId,
                    ErrorCode = "UNICONTA_ORDER_STATUS_FAILED",
                    Retryable = true,
                    SuggestedAction = "Auto-recovers on next tick; if it persists, retry SO via /admin/retry-sales-order."
                }, ct);
            }
        }

        // Failed in JD: status = Fejlet, record why, and consume the transfer trigger. It will only be
        // retried once a user fixes the issue and sets Xoverfor1 again (Group is then "Fejlet" or empty).
        int markedFailed = 0;
        foreach (var failure in result.Failures)
        {
            if (!failure.Item.SourceOrderNumber.HasValue) continue;
            var statusSw = Stopwatch.StartNew();
            var success = await uniconta.SetSalesOrderStatusAsync(failure.Item.SourceOrderNumber.Value, SalesOrderJdGroup.Failed, new Dictionary<string, object>
            {
                [UnicontaUserFields.IntegrationIssue] = string.IsNullOrWhiteSpace(failure.Message) ? "Ukendt fejl ved oprettelse i JD" : failure.Message,
                [UnicontaUserFields.SalesOrderTransferFlag] = false,
            }, ct);
            statusSw.Stop();
            timings.UnicontaStatusUpdateMs += statusSw.ElapsedMilliseconds;
            if (success)
            {
                markedFailed++;
            }
            else
            {
                logger.LogError("Failed to set SO {Order} group to Fejlet", failure.Item.SourceOrderNumber.Value);
                // Surface to integration_logs — without this, a silent Uniconta-side update failure
                // would leave xTransferToJD=true and the order would heal "by itself" on the next
                // tick without anyone knowing why, masking real Uniconta-side issues.
                await integrationLogger.LogAsync(new IntegrationLogEntry(
                    integrationLogger.IntegrationName, "warning", "Uniconta", failure.Item.SourceOrderNumber.Value.ToString(),
                    $"Failed to mark sales order {failure.Item.SourceOrderNumber} as Fejlet in Uniconta — xTransferToJD was NOT cleared and the order will be retried on the next tick.",
                    null, null)
                {
                    CorrelationId = logScope.CorrelationId,
                    ErrorCode = "UNICONTA_ORDER_STATUS_FAILED",
                    Retryable = true,
                    SuggestedAction = "Inspect Uniconta connection/auth state; the order will retry on its own but the original JD reject reason will be re-applied each tick until this clears."
                }, ct);
            }

            await integrationLogger.LogAsync(new IntegrationLogEntry(
                integrationLogger.IntegrationName, "error", "JD", failure.Item.SourceOrderNumber.Value.ToString(),
                $"JD rejected sales order {failure.Item.SourceOrderNumber}: {LogSanitizer.Sanitize(failure.Message)}", null,
                JsonSerializer.SerializeToElement(new { errorMessage = failure.Message, sourceOrderNumber = failure.Item.SourceOrderNumber }))
            {
                CorrelationId = logScope.CorrelationId,
                ErrorCode = "JD_VALIDATION_REJECTED",
                Retryable = false,
                SuggestedAction = "Manual review — Uniconta SO has been marked Fejlet with the JD reject reason."
            }, ct);
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
    /// Emits a monitoring row to Supabase on every failure path so the Hermes agent can see when
    /// orders are pushed to JD without a follow-along PDF.
    /// </summary>
    private async Task<IReadOnlyCollection<JdRequestOrderFileRef>> GenerateAndUploadDeliveryNotePdfAsync(
        LocalSalesOrder salesOrder,
        IntegrationLogScope logScope,
        RunTimings timings,
        CancellationToken cancellationToken)
    {
        var externalId = salesOrder.OrderNumber.ToString();
        try
        {
            logger.LogDebug("Generating PDF delivery note for order {OrderNumber}", salesOrder.OrderNumber);
            var pdfSw = Stopwatch.StartNew();
            var pdfBytes = await pdfService.GenerateDeliveryNotePdfAsync(salesOrder);
            pdfSw.Stop();
            timings.PdfGenMs += pdfSw.ElapsedMilliseconds;

            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                logger.LogWarning("PDF generation returned empty bytes for order {OrderNumber}", salesOrder.OrderNumber);
                timings.PdfsFailed++;
                await EmitPdfFailureAsync(externalId, "PDF_GENERATION_FAILED",
                    $"PDF delivery note for order {salesOrder.OrderNumber} was empty.",
                    "Inspect QuestPDF template — generator returned zero bytes.",
                    logScope, cancellationToken);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            var displayName = $"Folgeseddel_{salesOrder.OrderNumber}.pdf";
            var description = $"Delivery note for order {salesOrder.OrderNumber}";

            // Step 1: Create file metadata in JD and get pre-signed upload URL
            var createSw = Stopwatch.StartNew();
            var (ok, status, message, file, uploadUrl) = await jd.CreateFileAsync(
                displayName,
                description,
                cancellationToken);
            createSw.Stop();
            timings.JdCreateFileMs += createSw.ElapsedMilliseconds;

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                logger.LogWarning("Failed to create JD file for order {Order}. Status={Status} Message={Message}",
                    salesOrder.OrderNumber, status, message);
                timings.PdfsFailed++;
                await EmitPdfFailureAsync(externalId, "JD_VALIDATION_REJECTED",
                    $"JD refused to create file metadata for order {salesOrder.OrderNumber}: {LogSanitizer.Sanitize(message)} (status {status}).",
                    "Order will still ship to JD without a PDF on this tick; manual review.",
                    logScope, cancellationToken);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 2: Upload PDF content to pre-signed URL
            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, pdfBytes, "application/pdf", timings, cancellationToken);
            if (!uploadSucceeded)
            {
                logger.LogWarning("Failed to upload PDF content for JD file {FileId} order {Order}",
                    file.id, salesOrder.OrderNumber);
                timings.PdfsFailed++;
                await EmitPdfFailureAsync(externalId, "BLOB_UPLOAD_FAILED",
                    $"Azure Blob upload failed for JD file {file.id} (order {salesOrder.OrderNumber}).",
                    "Next tick will regenerate and retry the upload.",
                    logScope, cancellationToken);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            // Step 3: Verify file was uploaded successfully
            var verifySw = Stopwatch.StartNew();
            var verification = await jd.VerifyFileAsync(file.id, cancellationToken);
            verifySw.Stop();
            timings.JdVerifyFileMs += verifySw.ElapsedMilliseconds;
            if (!verification.ok)
            {
                logger.LogWarning("Failed to verify JD file {FileId} for order {Order}: {Message}",
                    file.id, salesOrder.OrderNumber, verification.message);
                timings.PdfsFailed++;
                await EmitPdfFailureAsync(externalId, "JD_VALIDATION_REJECTED",
                    $"JD refused to verify uploaded file {file.id} for order {salesOrder.OrderNumber}: {LogSanitizer.Sanitize(verification.message)}.",
                    "Next tick will regenerate and retry; if persistent, inspect JD file API logs.",
                    logScope, cancellationToken);
                return Array.Empty<JdRequestOrderFileRef>();
            }

            logger.LogInformation("Successfully generated and uploaded PDF delivery note as JD file {FileId} for order {Order}",
                file.id, salesOrder.OrderNumber);

            timings.PdfsSucceeded++;
            // Return file reference with packageLabel = true (important!)
            return new[]
            {
                new JdRequestOrderFileRef
                {
                    id = file.id,
                    packageLabel = true
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating or uploading PDF for order {Order}", salesOrder.OrderNumber);
            timings.PdfsFailed++;
            await EmitPdfFailureAsync(externalId, "PDF_GENERATION_FAILED",
                $"PDF pipeline threw for order {salesOrder.OrderNumber}: {LogSanitizer.Describe(ex)}",
                "Next tick will regenerate and retry; investigate App Insights for the full stack trace.",
                logScope, CancellationToken.None);
            return Array.Empty<JdRequestOrderFileRef>();
        }
    }

    private Task EmitPdfFailureAsync(string externalId, string errorCode, string message, string suggestedAction, IntegrationLogScope logScope, CancellationToken ct) =>
        integrationLogger.LogAsync(new IntegrationLogEntry(
            integrationLogger.IntegrationName, "warning", "Integration", externalId,
            message, null, null)
        {
            CorrelationId = logScope.CorrelationId,
            ErrorCode = errorCode,
            Retryable = true,
            SuggestedAction = suggestedAction
        }, ct);

    private async Task<IReadOnlyCollection<JdRequestOrderFileRef>> UploadDeliveryNotesAsync(
        string? debtorAccount,
        IReadOnlyDictionary<string, List<DebtorDeliveryNoteInfo>> deliveryNotesByDebtor,
        RunTimings timings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(debtorAccount)) return Array.Empty<JdRequestOrderFileRef>();
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

            var createSw = Stopwatch.StartNew();
            var (ok, status, message, file, uploadUrl) = await jd.CreateFileAsync(
                note.NoteName ?? $"DeliveryNote_{note.NoteNumber}",
                $"Delivery note for debtor {debtorAccount}",
                cancellationToken);
            createSw.Stop();
            timings.JdCreateFileMs += createSw.ElapsedMilliseconds;

            if (!ok || file == null || string.IsNullOrWhiteSpace(uploadUrl))
            {
                logger.LogWarning("Failed to create JD file for debtor {Debtor}. Status={Status} Message={Message}", debtorAccount, status, message);
                continue;
            }

            var uploadSucceeded = await UploadFileContentAsync(uploadUrl, note.FileData, note.MimeType, timings, cancellationToken);
            if (!uploadSucceeded)
            {
                logger.LogWarning("Failed to upload content for JD file {FileId} debtor {Debtor}", file.id, debtorAccount);
                continue;
            }

            var verifySw = Stopwatch.StartNew();
            var verification = await jd.VerifyFileAsync(file.id, cancellationToken);
            verifySw.Stop();
            timings.JdVerifyFileMs += verifySw.ElapsedMilliseconds;
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

    private static async Task<bool> UploadFileContentAsync(string uploadUrl, byte[] fileData, string? mimeType, RunTimings timings, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(fileData);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
        content.Headers.ContentLength = fileData.Length;

        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");

        var sw = Stopwatch.StartNew();
        using var response = await FileUploadHttpClient.SendAsync(request, cancellationToken);
        sw.Stop();
        timings.BlobUploadMs += sw.ElapsedMilliseconds;
        return response.IsSuccessStatusCode;
    }

    // Mutable per-run accumulator. Captured by the local function/methods to record phase timings
    // and counters; serialized into the final integration_logs row so diagnosis stays grep-able.
    private sealed class RunTimings
    {
        public long TotalRunMs { get; set; }
        public long UnicontaReadMs { get; set; }
        public long PdfGenMs { get; set; }
        public long JdCreateFileMs { get; set; }
        public long BlobUploadMs { get; set; }
        public long JdVerifyFileMs { get; set; }
        public long JdUpsertMs { get; set; }
        public long UnicontaStatusUpdateMs { get; set; }
        public int OrdersRead { get; set; }
        public int PdfsSucceeded { get; set; }
        public int PdfsFailed { get; set; }
        public int JdBatchCount { get; set; }
    }
}