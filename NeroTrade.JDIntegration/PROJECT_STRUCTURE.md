## Project structure and configuration guide (Azure Functions + Uniconta)

This document explains the folder structure, responsibilities, and how credentials/settings are handled. It is written to be reusable across similar integration projects that include Uniconta.

### Top-level layout

```text
├─ Program.cs                         // Azure Functions Worker bootstrap and DI setup
├─ host.json                          // Azure Functions host configuration
├─ Functions/
│  ├─ SyncDebtorsToJd.cs             // Timer/HTTP-triggered sync of Addresses (customers)
│  └─ SyncItemsToJd.cs               // Timer/HTTP-triggered sync of Catalog items
├─ Models/
│  ├─ ExternalIntegration/
│  │  ├─ Address.cs                   // JD Address DTOs
│  │  ├─ Catalog.cs                   // JD Catalog DTOs
│  │  └─ PagedResponse.cs             // JD paged wrapper { items, pagination }
│  └─ Uniconta/                       // (local models if needed)
├─ Services/
│  ├─ ExternalIntegration/
│  │  ├─ IJdLogisticsService.cs       // Business orchestration for JD
│  │  ├─ JdLogisticsService.cs        // Uses repositories; caches and upserts
│  │  └─ Repositories/
│  │     ├─ IJdRepository.cs          // Aggregated JD repository abstraction (addresses/catalog/orders)
│  │     └─ JdRepository.cs           // HttpClient-based implementation with retry/pagination
│  └─ UnicontaHandler/
│     ├─ IUnicontaService.cs          // Business facade (stream debtors/items)
│     ├─ UnicontaService.cs
│     ├─ UnicontaConnectionManager.cs // Session/login/OpenCompany and CrudAPI/QueryAPI factories
│     ├─ UnicontaConfig.cs
│     ├─ Repositories/
│     │  ├─ IUnicontaRepository.cs    // Aggregated Uniconta reads (debtors, items)
│     │  └─ UnicontaRepository.cs
│     ├─ Mappers/
│     │  ├─ DebtorMapper.cs           // Local debtor -> JD Address
│     │  └─ ItemMapper.cs             // Local inventory -> JD Catalog item
│     ├─ Models/
│     │  ├─ InventoryItem.cs
│     │  └─ LocalDebtor.cs
│     └─ README.md
└─ Properties/launchSettings.json
```

### Execution flow overview

- Functions trigger on schedule (or manually via HTTP during testing), fetch entities from Uniconta via repositories, map to JD DTOs, and call JD service to upsert.
- Uniconta connectivity and sessions are managed centrally in `UnicontaConnectionManager.cs` (login + OpenCompany) and exposed through `CrudAPI`/`QueryAPI` factories.

### Dependency injection and composition

`Program.cs` wires everything together:
- Binds `JdSettings` and `UnicontaConfig` from environment variables.
- Registers HttpClient-backed JD repository (`IJdRepository`) and orchestration service (`IJdLogisticsService`).
- Registers Uniconta connection manager, repository (`IUnicontaRepository`), and service (`IUnicontaService`).

You can reuse this pattern in any .NET Worker/Azure Functions app by copying the relevant folders and reproducing the DI registration.

## Credentials and settings

Keep secrets in environment variables or a secure provider (e.g., Azure Functions App Settings with Key Vault references). This project reads configuration as follows.

### JD Logistics API

Read by `JdRepository` at runtime using environment variables:

```bash
JD__BearerToken=...                            # Bearer token used for Authorization header
JD__BaseUrl=https://jd-api.testcode.dk/        # Base URL
JD__TimeoutSeconds=30                          # Optional
JD__DryRun=false                               # Optional (log-only)
```

Notes:
- The service sets default request headers on the shared `HttpClient` (e.g., `Authorization`, `Accept: application/json`).
- The dataset/endpoints can add their own `Authorization` header per request when needed.

### Uniconta configuration

Bound in `Program.cs` to `UnicontaConfig` using environment variables (double underscore maps to section properties):

```bash
UnicontaConfig__Username=...
UnicontaConfig__Password=...
UnicontaConfig__ApiKey=...        # GUID string
UnicontaConfig__CompanyId=...     # Integer
UnicontaConfig__BaseUrl=...       # Optional, defaults to https://api.uniconta.com
UnicontaConfig__TimeoutSeconds=30 # Optional
```

Behavior:
- On startup, required fields (`Username`, `Password`, `ApiKey`, `CompanyId`) are validated; the app throws with a clear message if missing.
- `UnicontaConnectionManager` uses these values to log in, open the target company, and create `CrudAPI`/`QueryAPI` instances.

### Local development vs. deployment

- Local: Use Azure Functions `local.settings.json` (not committed). Example `Values` section:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "EXTERNAL_API_KEY": "...",
    "EXTERNAL_API_ENDPOINT": "https://...",
    "EXTERNAL_DATASET_ENDPOINT": "https://...",
    "EXTERNAL_DATASET_API_KEY": "...",
    "UnicontaConfig__Username": "...",
    "UnicontaConfig__Password": "...",
    "UnicontaConfig__ApiKey": "...",
    "UnicontaConfig__CompanyId": "2289"
  }
}
```

- Cloud: Configure the same names as Application Settings on the Azure Function App. Prefer Key Vault references for secrets.

Note: `Program.cs` may include commented-out code for `appsettings.json`. If you prefer file-based config, you can enable that and still override with environment variables in production.

## Uniconta module reuse in other projects

To integrate Uniconta in another .NET Worker/Functions project:

1. Copy the `Services/UnicontaHandler/` folder (and `Models/Uniconta/Debtor.cs` if you use the same debtor model).
2. Register services in your composition root (Program/Main):
   - Add the `UnicontaConfig` singleton (read env vars or bind from config).
   - Add `UnicontaConnectionManager`, `IUnicontaService`, and `IUnicontaDebtorRepository`.
3. Provide the environment variables listed above.
4. Use `IUnicontaService` in your jobs/functions to create or check debtors. The connection manager encapsulates login/session/company selection.

Optional: If you also reuse an external integration module, copy `Services/ExternalIntegration/` and `Models/ExternalIntegration/`, register `IExternalIntegrationService`, and set the corresponding environment variables.

## Function scheduling and host

- `SyncDebtorsToJd` (Addresses) uses a `TimerTrigger` CRON expression. Prefer env-driven cron, e.g. `%CRON_ADDRESSES%` default `0 */10 * * * *`.
- `SyncItemsToJd` (Catalog) uses a `TimerTrigger` CRON expression. Prefer `%CRON_ITEMS%` default `0 5/10 * * * *`.
- Keep `host.json` minimal and environment-agnostic.

## Logging and diagnostics

- All services use `ILogger<T>` for structured logs. Secrets are not logged.
- Startup validation fails fast if critical settings are missing, making misconfiguration obvious.

## Security best practices

- Store secrets only in environment variables (local.settings.json for development; App Settings/Key Vault in cloud).
- Do not commit secrets. Avoid logging usernames, passwords, API keys, or tokens.
- Limit API keys to least privilege and rotate regularly.


