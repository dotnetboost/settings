# DotNetBoost.Settings

[![CI](https://github.com/dotnetboost/settings/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnetboost/settings/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DotNetBoost.Settings.Core.svg)](https://www.nuget.org/packages/DotNetBoost.Settings.Core)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dotnetboost/settings/blob/main/LICENSE)

**Change your app's settings while it is running, with no redeploy and no restart.**

You write a plain C# class. DotNetBoost.Settings stores it in your database, caches it, and gives
you a REST endpoint to edit it. The next read in your app sees the new value.

```csharp
[SettingGroup("mail-server")]
public class MailSettings
{
    public string Host { get; set; } = "smtp.example.com";
    public int Port { get; set; } = 587;
}

var mail = await settings.For<MailSettings>().GetAsync();   // always the current value
```

Works with **.NET 8 and .NET 10**, on **SQL Server, PostgreSQL, SQLite or MongoDB**.

---

## Get started in 5 minutes

This builds a minimal web app that stores its settings in a local SQLite file. Nothing else to install.

**1. Create a project and add the packages**

(`--prerelease` is needed until 1.0.0 is released.)

```bash
dotnet new web -n MyApp
cd MyApp
dotnet add package DotNetBoost.Settings.Core --prerelease
dotnet add package DotNetBoost.Settings.EntityFrameworkCore --prerelease
dotnet add package DotNetBoost.Settings.API --prerelease
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

**2. Replace `Program.cs` with this**

```csharp
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Store settings in SQLite through EF Core.
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=settings.db"));
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();

var app = builder.Build();

// Create the database and the settings tables on first run.
// In a real app, use EF Core migrations instead.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.MapSettingsEndpoints();   // GET/POST /api/settings/mail-server, plus the group list and schema

app.MapGet("/", async (ISettingManager settings) =>
{
    var mail = await settings.For<MailSettings>().GetAsync();
    return $"Sending mail through {mail.Host}:{mail.Port}";
});

app.Run();

// Your settings: a plain class. The property initialisers are the defaults.
[SettingGroup("mail-server")]
public class MailSettings
{
    public string Host { get; set; } = "smtp.example.com";
    public int Port { get; set; } = 587;
}

// Your EF Core context: add the two settings tables to it. One line, no interface
// and no DbSet properties — the engine is read off the context.
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettings(this);
}
```

**3. Run it and change a setting live**

```bash
dotnet run --urls http://localhost:5080
```

In a second terminal:

```bash
curl http://localhost:5080/
```

```text
Sending mail through smtp.example.com:587
```

```bash
curl -X POST http://localhost:5080/api/settings/mail-server -H "Content-Type: application/json" -d '{"host":"smtp.mycompany.com","port":2525}'
```

```bash
curl http://localhost:5080/
```

```text
Sending mail through smtp.mycompany.com:2525
```

The app picked up the new value without restarting, and it is saved in `settings.db`.

> ⚠️ **The settings endpoints allow anonymous access by default.** Before you deploy, protect them with
> `[Authorize]` on the settings class. See [Securing the endpoints](https://github.com/dotnetboost/settings/blob/main/docs/rest-api.md#securing-the-endpoints).

---

## Using it in your own code

Inject `ISettingManager` anywhere and read or write through `For<T>()`:

```csharp
public class EmailService(ISettingManager settings)
{
    public async Task SendAsync()
    {
        var mail = await settings.For<MailSettings>().GetAsync();        // whole object
        var port = await settings.For<MailSettings>().GetAsync(x => x.Port); // one property
        // ...
    }

    public Task ChangePortAsync(int port)
        => settings.For<MailSettings>().SetAsync(x => x.Port, port);
}
```

Reads are cached (10 minutes by default), so reading settings on every request is cheap. A write
clears the cache on the instance that made it. If you run more than one instance, add
`.SynchronizeThroughDistributedCache()` so the others pick the change up too — see
[Caching](https://github.com/dotnetboost/settings/blob/main/docs/caching.md#running-on-more-than-one-instance).

---

## Using your existing database

The quick start uses EF Core with SQLite because that needs no setup. In a real app, use the database you
already have:

| You use… | Package | Register with |
|---|---|---|
| Entity Framework Core | `DotNetBoost.Settings.EntityFrameworkCore` | `.UseEntityFrameworkCore<AppDbContext>()` |
| Dapper / plain ADO.NET | `DotNetBoost.Settings.Dapper` | `.UseDapper(sp => new SqlConnection(cs), migrateSchema: true)` |
| MongoDB | `DotNetBoost.Settings.MongoDb` | `.UseMongoDb("mongodb://localhost:27017", "my_app_db")` |

EF Core needs one line in `OnModelCreating` — no interface and no `DbSet` properties. See the
[storage providers guide](https://github.com/dotnetboost/settings/blob/main/docs/storage-providers.md)
for the full setup of each provider.

---

## What else it can do

Each feature is optional and switched on with one line on `AddSettings()`:

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAesEncryption(key)                          // encrypt [Sensitive] properties
    .UseFluentValidation(typeof(Program).Assembly)  // reject invalid values
    .OnChanged<MailSettings, MailSettingsChanged>() // react when a value changes
    .UseWriteObserver<MyWriteObserver>()            // record or forward every write
    .Build();
```

| I want to… | Guide |
|---|---|
| Use SQL Server, PostgreSQL or MongoDB instead of SQLite | [Storage providers](https://github.com/dotnetboost/settings/blob/main/docs/storage-providers.md) |
| Expose and secure the REST API | [REST API](https://github.com/dotnetboost/settings/blob/main/docs/rest-api.md) |
| Change what `GET` returns without changing what is stored | [Projectors](https://github.com/dotnetboost/settings/blob/main/docs/rest-api.md#reshaping-what-get-returns) |
| Reject invalid values (`[Range]`, FluentValidation) | [Validation](https://github.com/dotnetboost/settings/blob/main/docs/validation.md) |
| Encrypt passwords and API keys in the database | [Encryption](https://github.com/dotnetboost/settings/blob/main/docs/encryption.md) |
| Rename a settings class safely, set defaults | [Defining settings](https://github.com/dotnetboost/settings/blob/main/docs/defining-settings.md) |
| Run on several servers and keep their caches in step | [Caching](https://github.com/dotnetboost/settings/blob/main/docs/caching.md#running-on-more-than-one-instance) |
| Run code when a setting changes | [Change notifications](https://github.com/dotnetboost/settings/blob/main/docs/change-notifications.md) |
| Stop two people overwriting each other's edits | [Reading & writing](https://github.com/dotnetboost/settings/blob/main/docs/reading-and-writing.md#concurrent-writes) |
| Record or forward every write (and who made it) | [Write notifications](https://github.com/dotnetboost/settings/blob/main/docs/write-notifications.md) |
| Generate a form from the class (list groups, read the schema) | [Discovery endpoints](https://github.com/dotnetboost/settings/blob/main/docs/discovery-endpoints.md) |
| Give admins a web UI to edit settings | [Dashboard](https://github.com/dotnetboost/settings/blob/main/docs/dashboard.md) |
| See every builder option | [Configuration reference](https://github.com/dotnetboost/settings/blob/main/docs/configuration-reference.md) |
| Run the full demo stack (API, dashboard, PostgreSQL, Redis) | [.NET Aspire](https://github.com/dotnetboost/settings/blob/main/docs/aspire.md) |
| Understand how it works inside, or contribute | [Architecture](https://github.com/dotnetboost/settings/blob/main/docs/architecture.md) |

---

## Try the full demo

[`samples/SampleApp`](https://github.com/dotnetboost/settings/tree/main/samples/SampleApp) uses every
feature, with a web dashboard, PostgreSQL and Redis. If you have Docker running, one command starts all of it:

```bash
dotnet run --project aspire/DotNetBoost.Settings.AppHost
```

See [Running everything with .NET Aspire](https://github.com/dotnetboost/settings/blob/main/docs/aspire.md) for details.

---

## Packages

| Package | What it's for |
|---|---|
| `DotNetBoost.Settings.Core` | Required. The engine: `ISettingManager`, caching, encryption, notifications |
| `DotNetBoost.Settings.EntityFrameworkCore` | Store settings through EF Core (SQL Server, PostgreSQL, SQLite) |
| `DotNetBoost.Settings.Dapper` | Store settings through Dapper (SQL Server, PostgreSQL, SQLite) |
| `DotNetBoost.Settings.MongoDb` | Store settings in MongoDB |
| `DotNetBoost.Settings.FluentValidation` | Validate settings with FluentValidation |
| `DotNetBoost.Settings.API` | The generated `/api/settings/...` REST endpoints |

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](https://github.com/dotnetboost/settings/blob/main/CONTRIBUTING.md)
and the [architecture overview](https://github.com/dotnetboost/settings/blob/main/docs/architecture.md).

## License

MIT. See [LICENSE](https://github.com/dotnetboost/settings/blob/main/LICENSE).
