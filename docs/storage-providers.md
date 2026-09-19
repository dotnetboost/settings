# Storage providers

Pick one provider per application. Each is a separate package on top of `DotNetBoost.Settings.Core`.

| Provider | Databases | Creates its own tables? |
|---|---|---|
| [Entity Framework Core](#entity-framework-core) | SQL Server, PostgreSQL, SQLite | No: through your EF migrations |
| [Dapper](#dapper) | SQL Server, PostgreSQL, SQLite | Yes, with `migrateSchema: true` |
| [MongoDB](#mongodb) | MongoDB | Yes (collections are created on demand) |

## Entity Framework Core

```bash
dotnet add package DotNetBoost.Settings.EntityFrameworkCore --prerelease
```

Add the two settings tables to your `DbContext`:

```csharp
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), ISettingDbContext
{
    public DbSet<Setting>           Settings      => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder mb)
        => mb.ApplySettingsConfiguration(DatabaseProvider.Sqlite);
        // Options: SqlServer | PostgreSql | Sqlite
}
```

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();
```

Run `dotnet ef migrations add Init && dotnet ef database update` — this creates both the `Settings` and `SettingAudits` tables.

## Dapper

```bash
dotnet add package DotNetBoost.Settings.Dapper --prerelease
dotnet add package Microsoft.Data.SqlClient   # or Npgsql / Microsoft.Data.Sqlite
```

```csharp
builder.Services.AddSettings()
    .UseDapper(sp => new SqlConnection(connectionString), migrateSchema: true)
    .Build();
```

Supports `SqlConnection`, `NpgsqlConnection`, and `SqliteConnection`. `migrateSchema: true` auto-creates `Settings` and `SettingAudits` tables on startup.

## MongoDB

```bash
dotnet add package DotNetBoost.Settings.MongoDb --prerelease
```

```csharp
builder.Services.AddSettings()
    .UseMongoDb("mongodb://localhost:27017", "my_app_db")
    .Build();
```

The unique `(Group, Key)` index the store relies on is created once at startup by a hosted service. Pass `createIndexes: false` if the application's MongoDB user has no index-creation rights or you manage the index out of band — the store still assumes it exists.

**If your application already has a MongoDB client** — through .NET Aspire, or its own registration — pass a factory instead, so the settings store shares it rather than opening a second one:

```csharp
builder.AddMongoDBClient("settingsdb");          // Aspire, or your own AddSingleton<IMongoClient>

builder.Services.AddSettings()
    .UseMongoDb(sp => sp.GetRequiredService<IMongoClient>().GetDatabase("my_app_db"))
    .Build();
```

Either overload keeps `IMongoClient` and `IMongoDatabase` out of the container: the provider holds its database privately, so it can neither override nor be overridden by your application's own Mongo registration — whichever order they happen in.

