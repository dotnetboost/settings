using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using DotNetBoost.Settings.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

/// <summary>
/// Configured through the inferring overload, so the SQL Server provider name really does
/// map to <c>DatabaseProvider.SqlServer</c> — nvarchar(max) and a genuine rowversion — against
/// a live server rather than only in a unit test.
/// </summary>
public sealed class SqlServerDbContext(DbContextOptions<SqlServerDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettings(this);
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _contexts) await c.DisposeAsync();
    }
}
