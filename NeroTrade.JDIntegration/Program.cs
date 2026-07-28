using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using NeroTrade.JDIntegration.Services.PdfGeneration;
using NeroTrade.JDIntegration.Services.Logging;
using NeroTrade.JDIntegration.Services.Scheduling;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Optional: builder.ConfigurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

// Bind JD settings
builder.Services.Configure<JdSettings>(builder.Configuration.GetSection("JD"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JdSettings>>().Value);

// Status Mapping Config
builder.Services.Configure<StatusMappingConfig>(builder.Configuration.GetSection("StatusMapping"));

// Day/night sync scheduling. Bind the options, then register the scheduler that gates each sync
// function's heartbeat down to the configured per-job cadence (and picks the day/night session age).
builder.Services.Configure<SyncSchedulingOptions>(builder.Configuration.GetSection(SyncSchedulingOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SyncSchedulingOptions>>().Value);
builder.Services.AddSingleton<SyncScheduler>();

// Supabase integration logging
builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SupabaseOptions>>().Value);

// IntegrationLogScope is instantiated per-invocation inside each function — not via DI.
// Reason: the isolated worker does not create a fresh DI scope per timer trigger, so a Scoped
// lifetime collapses into a singleton and correlation ids leak across concurrent runs.

// Remediation endpoints (Phase 2). Shared secret is checked on every /admin/* call; endpoints
// refuse to run when SharedSecret is unset so the surface cannot accidentally go live unauthenticated.
builder.Services.Configure<RemediationOptions>(builder.Configuration.GetSection(RemediationOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RemediationOptions>>().Value);

var supabaseSection = builder.Configuration.GetSection("Supabase");
var supabaseBaseUrl = supabaseSection["BaseUrl"];
var supabaseServiceRoleKey = supabaseSection["ServiceRoleKey"];

if (!string.IsNullOrWhiteSpace(supabaseBaseUrl) && !string.IsNullOrWhiteSpace(supabaseServiceRoleKey))
{
    builder.Services.AddHttpClient<IIntegrationLogger, SupabaseIntegrationLogger>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<SupabaseOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/rest/v1/");
        client.DefaultRequestHeaders.Add("apikey", options.ServiceRoleKey);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    // Supabase not configured (e.g. local dev) — fall back to no-op logger so the main sync flow keeps running.
    builder.Services.AddSingleton<IIntegrationLogger, NoOpIntegrationLogger>();
}

// Typed HttpClient + repositories for JD
builder.Services.AddHttpClient<IJdRepository, JdRepository>();
builder.Services.AddSingleton<JdReadCache>();
builder.Services.AddScoped<IJdLogisticsService, JdLogisticsService>();

// Uniconta services
builder.Services.AddSingleton<UnicontaConfig>(_ => UnicontaConfig.FromEnvironment());
builder.Services.AddSingleton<UnicontaConnectionManager>();
builder.Services.AddScoped<IUnicontaService, NeroTrade.JDIntegration.Services.UnicontaHandler.UnicontaService>();
builder.Services.AddScoped<IUnicontaRepository, UnicontaRepository>();

// Mapper
builder.Services.AddSingleton<DebtorMapper>();
builder.Services.AddSingleton<ItemMapper>();
builder.Services.AddSingleton<SalesOrderMapper>();
builder.Services.AddSingleton<PurchaseOrderMapper>();

// PDF Generation
builder.Services.AddSingleton<IDeliveryNotePdfService, DeliveryNotePdfService>();

// Sync jobs. They no longer carry their own [Function]/[TimerTrigger] — SyncDispatcher owns the single
// timer and calls them — so the runtime does not construct them for us and they must be registered here.
// Scoped, so one dispatcher invocation shares one IUnicontaRepository/IUnicontaService with all six.
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncSalesOrdersToJd>();
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncPurchaseOrdersToJd>();
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncPostedPurchaseInvoicesToJd>();
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncItemsToJd>();
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncRequestOrderStatusToUniconta>();
builder.Services.AddScoped<NeroTrade.JDIntegration.Functions.SyncReceivedQuantityToUniconta>();

builder.Build().Run();