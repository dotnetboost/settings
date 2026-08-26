// ─────────────────────────────────────────────────────────────────────────────
//  DotNetBoost.Settings — Aspire AppHost
//
//  Starts the whole stack with one `dotnet run --project` here — no Aspire CLI required,
//  though `aspire run` does the same if you have it installed:
//
//    • the storage container backing the settings store  (PostgreSQL by default)
//    • Redis, used by the sample's custom ISettingCache
//    • the API      — samples/SampleApp
//    • the dashboard — clients/dashboard (Nuxt)
//
//  Exactly ONE storage-provider block below is active. Each alternative is written
//  out in full and commented; to switch, comment the active block, uncomment the one
//  you want, uncomment its PackageReference in DotNetBoost.Settings.AppHost.csproj,
//  and flip the matching block in samples/SampleApp/Program.cs.
// ─────────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// ── Secrets ──────────────────────────────────────────────────────────────────
// The AES-256 key used to encrypt [Sensitive] properties at rest. The value lives in
// appsettings.json for local development only; override it for anything real with
//   dotnet user-secrets set Parameters:settings-encryption-key "<base64 32 bytes>"
// run from this directory.
var encryptionKey = builder.AddParameter("settings-encryption-key", secret: true);

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ACTIVE: PostgreSQL (Entity Framework Core)
//  Package: Aspire.Hosting.PostgreSQL
// ─────────────────────────────────────────────────────────────────────────────
var postgres = builder.AddPostgres("postgres")
    // Named volume, so settings written in one session survive the next run.
    .WithDataVolume("dotnetboost-settings-pgdata")
    // Keeps the container alive between AppHost runs — a cold start costs a few seconds.
    .WithLifetime(ContainerLifetime.Persistent)
    // Browser-based SQL client at its own dashboard link; handy for inspecting the tables.
    .WithPgWeb();

var settingsDb = postgres.AddDatabase("settingsdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: MongoDB
//  Package: Aspire.Hosting.MongoDB
// ─────────────────────────────────────────────────────────────────────────────
// var mongo = builder.AddMongoDB("mongo")
//     .WithDataVolume("dotnetboost-settings-mongodata")
//     .WithLifetime(ContainerLifetime.Persistent)
//     .WithMongoExpress();
//
// var settingsDb = mongo.AddDatabase("settingsdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: SQL Server
//  Package: Aspire.Hosting.SqlServer
// ─────────────────────────────────────────────────────────────────────────────
// var sqlServer = builder.AddSqlServer("sqlserver")
//     .WithDataVolume("dotnetboost-settings-mssqldata")
//     .WithLifetime(ContainerLifetime.Persistent);
//
// var settingsDb = sqlServer.AddDatabase("settingsdb");

// ─────────────────────────────────────────────────────────────────────────────
//  Storage provider — ALTERNATIVE: SQLite
//  No container and no hosting package: SQLite is a file next to the API. Delete the
//  WithReference(settingsDb)/WaitFor(settingsDb) lines on the api resource below and
//  let samples/SampleApp/Program.cs point at its own "Data Source=sample.db".
// ─────────────────────────────────────────────────────────────────────────────

// ── Cache ────────────────────────────────────────────────────────────────────
// Backs the sample's RedisSettingCache (an ISettingCache registered through
// .UseCustomCache<T>()). Comment this out — along with the UseCustomCache line in
// samples/SampleApp/Program.cs — to fall back to the built-in IMemoryCache.
var redis = builder.AddRedis("cache")
    .WithLifetime(ContainerLifetime.Persistent)
    // Browser UI for browsing keys, watching evictions and confirming the cache is used.
    .WithRedisInsight();

// ── API — samples/SampleApp ──────────────────────────────────────────────────
// Its endpoints come from samples/SampleApp/Properties/launchSettings.json. Without that
// file the resource has no endpoint at all, and api.GetEndpoint("http") below — the URL
// the dashboard is handed — has nothing to resolve to.
var api = builder.AddProject<Projects.SampleApp>("api")
    .WithReference(settingsDb)
    .WaitFor(settingsDb)
    .WithReference(redis)
    .WaitFor(redis)
    .WithEnvironment("Settings__EncryptionKey", encryptionKey)
    .WithExternalHttpEndpoints();

// ── Dashboard — clients/dashboard (Nuxt) ─────────────────────────────────────
// npm install runs automatically before the dev server starts.
builder.AddJavaScriptApp("dashboard", "../../clients/dashboard", "dev")
    .WithNpm()
    // Nuxt binds whatever PORT says; without this it picks its own and Aspire has no
    // endpoint to publish, so the dashboard never gets a clickable URL.
    .WithHttpEndpoint(env: "PORT")
    .WithReference(api)
    .WaitFor(api)
    // Consumed by server/api/settings/[...path].ts, which proxies the browser's calls.
    .WithEnvironment("NUXT_SETTINGS_API_URL", api.GetEndpoint("http"))
    // The sidebar's "API reference" link. Read in the browser, so it needs the public URL.
    .WithEnvironment("NUXT_PUBLIC_API_REFERENCE_URL",
        ReferenceExpression.Create($"{api.GetEndpoint("http")}/scalar"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
