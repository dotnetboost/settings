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

Add the two settings tables to your `DbContext`. That one call is the whole registration —
your context needs no interface and no `DbSet` properties, because the store reaches its
entities through `Set<T>()`:

```csharp
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder mb)
        => mb.ApplySettings(this);
}
```

The engine — which decides the `Value` column type and how `RowVersion` is mapped — is read
off the context, so switching provider needs no matching edit here. Name it explicitly when
you need to override that, for a model built against one engine and migrated onto another:

```csharp
protected override void OnModelCreating(ModelBuilder mb)
    => mb.ApplySettings(DatabaseProvider.SqlServer);
    // Options: SqlServer | PostgreSql | Sqlite
```

> **Upgrading from `1.0.0-preview.1`?** `ISettingDbContext` is obsolete and does nothing.
> Delete `: ISettingDbContext` and the two `DbSet` properties; nothing else changes.
> `UseEntityFrameworkCore<TContext>()` registers nothing against `DbContext` itself, so the
> library never claims a framework type it does not own.

```csharp
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .Build();
```

Run `dotnet ef migrations add Init && dotnet ef database update` — this creates the `Settings` table.

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

Supports `SqlConnection`, `NpgsqlConnection`, and `SqliteConnection`. `migrateSchema: true` auto-creates the `Settings` table on startup.

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

