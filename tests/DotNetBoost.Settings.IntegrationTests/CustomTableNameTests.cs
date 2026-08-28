using Dapper;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Dapper;
using DotNetBoost.Settings.EntityFrameworkCore;
using DotNetBoost.Settings.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace DotNetBoost.Settings.IntegrationTests;

public sealed class CustomNamesDbContext(DbContextOptions<CustomNamesDbContext> options)
    : DbContext(options), Settings.EntityFrameworkCore.ISettingDbContext
{
    public DbSet<Setting>           Settings      => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.PostgreSql, o =>
        {
            o.Schema        = "cfg";
            o.SettingsTable = "AppSettings";
            o.AuditTable    = "AppSettingAudits";
        });
}

/// <summary>
/// Custom object names have to actually reach the database — validating the strings proves
/// nothing on its own. These assert against the server's own catalog.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperCustomTableNameTests(PostgreSqlFixture fixture)
{
    private static SettingTableOptions Custom() => new()
    {
        Schema        = "cfg",
        SettingsTable = "AppSettings",
        AuditTable    = "AppSettingAudits"
    };

    [Fact]
    public async Task SchemaInitializer_CreatesTheSchemaAndTheCustomTables()
    {
        await using var conn = new NpgsqlConnection(await fixture.CreateDatabaseAsync());
        await conn.OpenAsync();

        await DapperSchemaInitializer.InitializeAsync(conn, Custom());

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'cfg'")).ToList();

        Assert.Contains("appsettings", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("appsettingaudits", tables, StringComparer.OrdinalIgnoreCase);

        // And nothing landed under the default names in the default schema.
        var publicTables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'")).ToList();
        Assert.DoesNotContain("settings", publicTables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Store_ReadsAndWritesTheCustomTable()
    {
        await using var conn = new NpgsqlConnection(await fixture.CreateDatabaseAsync());
        await conn.OpenAsync();
        await DapperSchemaInitializer.InitializeAsync(conn, Custom());

        var store = new DapperSettingStore(conn, Custom());
        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" });

        Assert.Equal("smtp", (await store.GetAsync("Mail", "Host"))!.Value);

        // Prove the row is in cfg.AppSettings specifically, not somewhere the store merely agrees on.
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cfg.AppSettings"));
    }

    [Fact]
    public async Task IndexName_IsDerivedFromTheTableName()
    {
        await using var conn = new NpgsqlConnection(await fixture.CreateDatabaseAsync());
        await conn.OpenAsync();
        await DapperSchemaInitializer.InitializeAsync(conn, Custom());

        var indexes = (await conn.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'cfg'")).ToList();

        Assert.Contains("ux_appsettings_group_key", indexes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EfCore_MapsToTheCustomSchemaAndTables()
    {
        var options = new DbContextOptionsBuilder<CustomNamesDbContext>()
            .UseNpgsql(await fixture.CreateDatabaseAsync()).Options;

        await using var ctx = new CustomNamesDbContext(options);
        await ctx.Database.EnsureCreatedAsync();

        var store = new EfCoreSettingStore(ctx);
        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" });

        await using var raw = new NpgsqlConnection(ctx.Database.GetConnectionString());
        await raw.OpenAsync();
        Assert.Equal(1, await raw.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM cfg.\"AppSettings\""));
    }
}

/// <summary>The MongoDB equivalent: the collection name must reach the server.</summary>
[Collection(MongoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoCustomCollectionTests(MongoDbFixture fixture)
{
    [Fact]
    public async Task Store_UsesTheConfiguredCollection()
    {
        var db = fixture.CreateDatabase();
        await MongoSettingStore.EnsureIndexesAsync(db, "app_settings");

        var store = new MongoSettingStore(db, "app_settings");
        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" });

        var names = await (await db.ListCollectionNamesAsync()).ToListAsync();
        Assert.Contains("app_settings", names, StringComparer.Ordinal);
        Assert.DoesNotContain("settings", names, StringComparer.Ordinal);

        Assert.Equal(1, await db.GetCollection<BsonDocument>("app_settings").CountDocumentsAsync(new BsonDocument()));
    }

    [Theory]
    [InlineData("has$dollar")]
    [InlineData("system.profile")]
    [InlineData("")]
    public void Store_RejectsIllegalCollectionNames(string name)
        => Assert.ThrowsAny<ArgumentException>(() => new MongoSettingStore(fixture.CreateDatabase(), name));
}
