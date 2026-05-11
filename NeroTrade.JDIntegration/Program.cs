using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeroTrade.JDIntegration.Models.Settings;
using NeroTrade.JDIntegration.Services.ExternalIntegration;
using NeroTrade.JDIntegration.Services.UnicontaHandler;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Repositories;
using NeroTrade.JDIntegration.Services.UnicontaHandler.Mappers;
using NeroTrade.JDIntegration.Services.ExternalIntegration.Repositories;
using NeroTrade.JDIntegration.Services.PdfGeneration;

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