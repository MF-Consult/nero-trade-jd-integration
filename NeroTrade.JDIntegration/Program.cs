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

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Optional: builder.ConfigurationBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

// Bind JD settings
builder.Services.Configure<JdSettings>(builder.Configuration.GetSection("JD"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JdSettings>>().Value);

// Status Mapping Config
builder.Services.Configure<StatusMappingConfig>(builder.Configuration.GetSection("StatusMapping"));

// Supabase integration logging
builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<SupabaseOptions>>().Value);
builder.Services.AddHttpClient<IIntegrationLogger, SupabaseIntegrationLogger>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SupabaseOptions>>().Value;
    if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.ServiceRoleKey))
    {
        throw new InvalidOperationException(
            "Supabase:BaseUrl and Supabase:ServiceRoleKey must be configured.");
    }

    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/rest/v1/");
    client.DefaultRequestHeaders.Add("apikey", options.ServiceRoleKey);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ServiceRoleKey);
    client.Timeout = TimeSpan.FromSeconds(30);
});

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

builder.Build().Run();