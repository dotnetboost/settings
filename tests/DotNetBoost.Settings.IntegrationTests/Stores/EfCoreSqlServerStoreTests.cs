using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using DotNetBoost.Settings.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

public sealed class SqlServerDbContext(DbContextOptions<SqlServerDbContext> options)
    : DbContext(options), ISettingDbContext
{
    public DbSet<Setting>           Settings      => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.SqlServer);
}

/// <summary>
/// Runs the full <see cref="SettingStoreContractTests"/> suite against the EF Core store on a
/// real SQL Server, exercising the SqlServer branch of the model configuration — the
/// nvarchar(max) value column and, uniquely to this provider, a genuine rowversion column
/// rather than a plain concurrency token.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCoreSqlServerStoreTests(SqlServerFixture fixture)
    : SettingStoreContractTests, IAsyncLifetime
{
    private readonly List<SqlServerDbContext> _contexts = [];

    protected override async Task<ISettingStore> CreateStoreAsync()
        => new EfCoreSettingStore(await NewContextAsync());

    private async Task<SqlServerDbContext> NewContextAsync()
    {
        var options = new DbContextOptionsBuilder<SqlServerDbContext>()
            .UseSqlServer(await fixture.CreateDatabaseAsync())
            .Options;

        var ctx = new SqlServerDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        _contexts.Add(ctx);
        return ctx;
    }

    /// <summary>
    /// SettingConfiguration maps RowVersion with IsRowVersion() on SQL Server only. This
    /// asserts the database really populates it, which is what the mapping is for.
    /// </summary>
    [Fact]
    public async Task RowVersion_IsPopulatedByTheDatabase()
    {
        var ctx   = await NewContextAsync();
        var store = new EfCoreSettingStore(ctx);

        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" });

        var reread = await store.GetAsync("Mail", "Host");
        Assert.NotNull(reread!.RowVersion);
        Assert.NotEmpty(reread.RowVersion!);
    }

    [Fact]
    public async Task ValueColumn_AcceptsPayloadsBeyondNVarCharLimit()
    {
        // nvarchar(max), not nvarchar(4000) — a JSON-serialised setting can be arbitrarily large.
        var store = new EfCoreSettingStore(await NewContextAsync());
        var big   = new string('x', 20_000);

        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Blob", Value = big, Type = "System.String" });

        Assert.Equal(big, (await store.GetAsync("Mail", "Blob"))!.Value);
    }

    [Fact]
    public async Task AuditStore_RecordsAndReadsBackHistory()
    {
        var ctx   = await NewContextAsync();
        var audit = new EfCoreAuditStore(ctx);

        await audit.RecordAsync(new SettingAuditEntry
        {
            Group = "Mail", Key = "Host", OldValue = "old", NewValue = "new", ChangedBy = "tester"
        });

        var history = await audit.GetHistoryAsync("Mail", "Host");
        Assert.Equal("old", Assert.Single(history).OldValue);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _contexts) await c.DisposeAsync();
    }
}
