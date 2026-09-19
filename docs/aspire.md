# Running everything with .NET Aspire

[`aspire/DotNetBoost.Settings.AppHost`](../aspire/DotNetBoost.Settings.AppHost) orchestrates the whole
stack — API, dashboard and every backing service — from one command:

```bash
dotnet run --project aspire/DotNetBoost.Settings.AppHost
```

That needs nothing installed beyond the .NET 10 SDK and a running container engine (Docker or
Podman) — the AppHost pulls the orchestrator and dashboard from NuGet via `dnx aspire.cli` on first
run. Watch the console for the dashboard link; it carries a one-time login token.

The Aspire CLI is optional and shortens the command to `aspire run`. Install it whichever way suits
you — the first needs no elevation and no new tool chain:

```bash
dotnet tool install -g Aspire.Cli
```

```bash
brew install --cask microsoft/aspire/aspire
```

Also available as `npm install -g @microsoft/aspire-cli`, `winget install Microsoft.Aspire`, or the
script at [get.aspire.dev](https://get.aspire.dev). `aspire run` works from anywhere in the repo —
[`aspire.config.json`](../aspire.config.json) points it at the AppHost.

What comes up:

| Resource | What it is |
|---|---|
| `postgres` / `settingsdb` | PostgreSQL 18 container, named volume, persistent across runs |
| `pgweb` | Browser SQL client for inspecting `Settings` and `SettingAudits` |
| `cache` | Redis container backing `RedisSettingCache` |
| `redisinsight` | Browser UI for watching the cache fill and expire |
| `api` | `samples/SampleApp` — the REST endpoints and the Scalar reference at `/scalar` |
| `dashboard-installer` | `npm install` for the Nuxt client, run once before the dev server |
| `dashboard` | `clients/dashboard` — `npm run dev`, wired to the API automatically |

The Aspire dashboard lists every resource with its URL, console output, environment, and the
OpenTelemetry traces, metrics and structured logs the API emits through
[`DotNetBoost.Settings.ServiceDefaults`](../aspire/DotNetBoost.Settings.ServiceDefaults).

Nothing is configured by hand. The AppHost injects `ConnectionStrings__settingsdb` and
`ConnectionStrings__cache` into the API, and `NUXT_SETTINGS_API_URL` /
`NUXT_PUBLIC_API_REFERENCE_URL` into the dashboard, so the values in
`clients/dashboard/.env.example` are only needed when you run the two halves separately.

## Switching the storage provider

The AppHost starts PostgreSQL and the API talks to it through EF Core. Every other provider is
written out in full and commented, so switching is four edits and no new code:

| File | What to change |
|---|---|
| `aspire/…/AppHost.cs` | Comment the active provider block, uncomment another |
| `aspire/…/DotNetBoost.Settings.AppHost.csproj` | Uncomment that provider's `Aspire.Hosting.*` package |
| `samples/SampleApp/SampleApp.csproj` | Uncomment the matching `ItemGroup` |
| `samples/SampleApp/Program.cs` | Comment the active block, uncomment the matching one |

The available blocks are PostgreSQL (EF Core, active), SQL Server (EF Core), SQLite (EF Core, no
container), MongoDB, and Dapper on PostgreSQL. For the EF Core ones, also flip the
`DatabaseProvider` passed to `ApplySettingsConfiguration` in `samples/SampleApp/AppDbContext.cs` —
it decides the column type used for `Value` and how `RowVersion` is mapped.

## The Redis cache

`samples/SampleApp/Caching/RedisSettingCache.cs` is a real
[`ISettingCache`](../src/DotNetBoost.Settings.Core/Interfaces/ISettingCache.cs) over Redis, registered
with `.UseCustomCache<RedisSettingCache>()`. It exists because the default `IMemoryCache` gives every
API instance its own copy: a write on one leaves the others serving stale values until their entry
expires. Redis makes the update visible fleet-wide at once. Drop the `AddRedisClient("cache")` and
`.UseCustomCache<…>()` lines in `Program.cs`, plus the `cache` resource in `AppHost.cs`, to fall back
to the in-memory cache.

## Secrets

The AES key protecting `[Sensitive]` properties comes from the `settings-encryption-key` parameter,
which the AppHost passes to the API as `Settings__EncryptionKey`. Its development value lives in
`aspire/DotNetBoost.Settings.AppHost/appsettings.json` — a throwaway key for local containers only.
Override it anywhere real:

```bash
dotnet user-secrets set Parameters:settings-encryption-key "$(openssl rand -base64 32)" --project aspire/DotNetBoost.Settings.AppHost
```

Rotating the key makes values already encrypted under the old one unreadable, so the persistent
Postgres volume and the key belong together.

