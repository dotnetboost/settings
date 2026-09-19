# Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  Your Application                                                 │
│  ISettingManager.For<T>() → ISettingAccessor<T>                   │
└───────────────────────────┬────────────────────────────────────┘
                             │
                      SettingManager
        ┌───────────┬────────┼────────┬─────────────┐
        │           │        │        │             │
  ISettingCache  ISettingStore  ISettingEncryptor  ISettingAuditStore
  (IMemoryCache   (EF Core /      (AES-256-GCM       (EfCoreAuditStore
   or Redis)       Dapper /        or custom)          or custom)
                   MongoDB)
                             │
                   ISettingChangedHandler<T>
                   (your app's runtime reactions)
```

## Repository layout

```
dotnetboost/
├── src/                                       # Library projects (each = 1 NuGet package)
│   ├── DotNetBoost.Settings.Core/
│   ├── DotNetBoost.Settings.EntityFrameworkCore/
│   ├── DotNetBoost.Settings.Dapper/
│   ├── DotNetBoost.Settings.MongoDb/
│   ├── DotNetBoost.Settings.FluentValidation/
│   └── DotNetBoost.Settings.API/
├── tests/
│   ├── DotNetBoost.Settings.UnitTests/         # Core logic, mocked stores
│   ├── DotNetBoost.Settings.ProviderTests/     # Store contract, in-process SQLite
│   ├── DotNetBoost.Settings.IntegrationTests/  # Same contract, real engines (needs Docker)
│   └── DotNetBoost.Settings.ApiTests/          # Minimal-API endpoints via TestHost
├── samples/
│   └── SampleApp/                              # Runnable end-to-end demo
├── clients/
│   └── dashboard/                              # Nuxt 4 settings dashboard (SPA client)
├── aspire/
│   ├── DotNetBoost.Settings.AppHost/           # Orchestrates API + dashboard + containers
│   └── DotNetBoost.Settings.ServiceDefaults/   # OTel, health checks, service discovery
├── docs/
├── .github/
│   ├── workflows/ci.yml
│   └── ISSUE_TEMPLATE/
├── Directory.Build.props                       # Shared MSBuild settings (net10.0, analyzers)
├── Directory.Packages.props                    # Central package version management
├── DotNetBoost.Settings.sln
├── aspire.config.json                          # Points `aspire run` at the AppHost
├── CHANGELOG.md
├── CONTRIBUTING.md
└── README.md
```

## Testing

```bash
dotnet test                                             # everything (integration tests need Docker)
dotnet test --filter "Category!=Integration"            # skip the container-backed suites
dotnet test --collect:"XPlat Code Coverage"             # with coverage
dotnet test tests/DotNetBoost.Settings.ProviderTests    # store contract on SQLite only
```

Adding a new store provider? Inherit `SettingStoreContractTests` and implement `CreateStoreAsync()` — you get 24 behavioural tests (upsert, delete, count, exists, etc.) for free, guaranteeing your provider behaves identically to the built-in ones.

