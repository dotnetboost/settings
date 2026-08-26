using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using SampleApp;
using SampleApp.Caching;
using SampleApp.Settings;
using Scalar.AspNetCore;
using System.Reflection;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, service discovery and resilient HttpClients. Everything
// this registers is inert when the app runs standalone; under the AppHost it is what
// makes the traces, metrics and logs show up on the Aspire dashboard.
builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ACTIVE: PostgreSQL (Entity Framework Core)
//
//  "settingsdb" is the resource name from aspire/DotNetBoost.Settings.AppHost/AppHost.cs;
//  the AppHost injects ConnectionStrings__settingsdb pointing at its container.
//  Every alternative below is written out in full and commented — to switch, comment this
//  block, uncomment the one you want, flip the matching ItemGroup in SampleApp.csproj,
//  the provider block in AppHost.cs, and the DatabaseProvider in AppDbContext.cs.
// ─────────────────────────────────────────────────────────────────────────────
builder.AddNpgsqlDbContext<AppDbContext>("settingsdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: SQL Server (Entity Framework Core)
//  SampleApp.csproj: Microsoft.EntityFrameworkCore.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// builder.Services.AddDbContext<AppDbContext>(opt =>
//     opt.UseSqlServer(builder.Configuration.GetConnectionString("settingsdb")));

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: SQLite (Entity Framework Core, no container)
//  SampleApp.csproj: Microsoft.EntityFrameworkCore.Sqlite
//  This is the zero-infrastructure option: `dotnet run` here with no AppHost at all.
// ─────────────────────────────────────────────────────────────────────────────
// builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlite("Data Source=sample.db"));

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: MongoDB
//  SampleApp.csproj: DotNetBoost.Settings.MongoDb project reference
//  No DbContext is involved, so AppDbContext.cs can be deleted along with the
//  EnsureCreatedAsync block further down.
// ─────────────────────────────────────────────────────────────────────────────
// var mongoConnectionString = builder.Configuration.GetConnectionString("settingsdb")
//     ?? throw new InvalidOperationException("Connection string 'settingsdb' is missing.");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: Dapper on PostgreSQL
//  SampleApp.csproj: DotNetBoost.Settings.Dapper project reference + Aspire.Npgsql
//  AddNpgsqlDataSource gives Dapper a pooled, instrumented NpgsqlDataSource to open
//  connections from; migrateSchema: true creates the tables on first start.
// ─────────────────────────────────────────────────────────────────────────────
// builder.AddNpgsqlDataSource("settingsdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Cache — ACTIVE: Redis
//  Registers IConnectionMultiplexer against the "cache" resource from the AppHost.
//  Comment this out (and the .UseCustomCache line below) for the built-in IMemoryCache.
// ─────────────────────────────────────────────────────────────────────────────
builder.AddRedisClient("cache");

// The AES-256 key protecting [Sensitive] properties at rest. Under the AppHost this
// arrives as the settings-encryption-key parameter; standalone it falls back to a
// throwaway key, which means anything already encrypted becomes unreadable on restart.
// In production, load it from a secret manager — never hardcode it.
var encryptionKey = builder.Configuration["Settings:EncryptionKey"]
    ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

var settings = builder.Services.AddSettings();

// ── Store registration, one line per provider ────────────────────────────────
// EfCoreAuditStore is what backs GET /api/settings/{route}/audit. ISettingAuditStore is
// optional, so a provider without one simply records no history — MongoDB has no
// implementation yet, and the Dapper store writes its SettingAudits table only when its
// own schema migration has run.
settings.UseEntityFrameworkCore<AppDbContext>()
        .UseAuditStore<DotNetBoost.Settings.EntityFrameworkCore.EfCoreAuditStore>();

// MongoDB:  settings.UseMongoDb(mongoConnectionString, databaseName: "settingsdb");
// Dapper:   (add `using Npgsql;` at the top)
//           settings.UseDapper(
//               sp => sp.GetRequiredService<NpgsqlDataSource>().CreateConnection(),
//               migrateSchema: true);

settings
    // Swap the in-memory cache for the shared Redis one. Drop this line and the
    // AddRedisClient call above to go back to IMemoryCache.
    .UseCustomCache<RedisSettingCache>()
    .UseAesEncryption(encryptionKey)
    .UseFluentValidation(Assembly.GetExecutingAssembly())
    .OnChanged<MailSettings, MailSettingsChangedHandler>()
    .Build();

builder.Services.AddScoped<MailSettingsChangedHandler>();

// Generates the OpenAPI document that the Scalar UI reads. The settings endpoints
// already carry WithName/WithSummary/Produces metadata, so they document themselves.
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title       = "DotNetBoost.Settings — Sample API";
        document.Info.Version     = "v1";
        document.Info.Description =
            "Runtime settings served from PostgreSQL. Each [SettingGroup] class is exposed as "
            + "GET (read), POST (update) and GET /audit (change history). Properties marked "
            + "[Sensitive] are encrypted at rest and shown as \"[encrypted]\" in the audit trail.";
        return Task.CompletedTask;
    }));

var app = builder.Build();

// /health and /alive, polled by the AppHost to decide when the dashboard may start.
app.MapDefaultEndpoints();

// EF Core providers only — MongoDB creates collections on demand and the Dapper store
// builds its own schema when registered with migrateSchema: true.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Interactive API reference at /scalar — use it to try the endpoints below.
app.MapOpenApi();
app.MapScalarApiReference(options => options
    .WithTitle("DotNetBoost.Settings — Sample API")
    .WithTheme(ScalarTheme.Purple));

// The endpoints the dashboard in clients/dashboard talks to:
// GET/POST api/settings/{route} per [SettingGroup].
app.MapSettingsEndpoints();

app.MapGet("/", async (ISettingManager settings) =>
{
    var mail = await settings.For<MailSettings>().GetAsync();
    return Results.Ok(new
    {
        mail.Host,
        mail.Port,
        mail.UseSsl,
        PasswordIsSet = !string.IsNullOrEmpty(mail.Password)
    });
});

app.Run();
