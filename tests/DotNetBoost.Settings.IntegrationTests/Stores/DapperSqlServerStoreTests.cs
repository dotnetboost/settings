using Dapper;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Dapper;
using DotNetBoost.Settings.ProviderTests.Stores;
using Microsoft.Data.SqlClient;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

/// <summary>
/// Runs the full <see cref="SettingStoreContractTests"/> suite against the Dapper store on a
/// real SQL Server. This is the only coverage of the SqlServer branch of
/// <c>DapperSchemaInitializer</c> and of the T-SQL upsert — both of which are hand-written
/// per dialect, so nothing but executing them against the engine proves they parse.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperSqlServerStoreTests(SqlServerFixture fixture)
    : SettingStoreContractTests, IAsyncLifetime
{
    private readonly List<SqlConnection> _connections = [];

    protected override async Task<ISettingStore> CreateStoreAsync()
        => new DapperSettingStore(await NewConnectionAsync());

    private async Task<SqlConnection> NewConnectionAsync()
        => await OpenAsync(await fixture.CreateDatabaseAsync());

    private async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        _connections.Add(connection);

        // Exercises the real SqlServer DDL shipped with the provider.
        await DapperSchemaInitializer.InitializeAsync(connection);
        return connection;
    }

    [Fact]
    public async Task SchemaInitializer_CreatesBothTables_AndIsIdempotent()
    {
        var connection = await NewConnectionAsync();

        // Running it a second time must not throw: the hosted service calls it on every boot.
        // The SqlServer script guards with IF OBJECT_ID(...) IS NULL rather than IF NOT EXISTS.
        await DapperSchemaInitializer.InitializeAsync(connection);

        var tables = await connection.QueryAsync<string>(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'");

        Assert.Contains("Settings", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SettingAudits", tables, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The upsert is a single multi-statement batch: an UPDATE followed by a conditional
    /// INSERT whose SELECT has no FROM clause. That construct is dialect-sensitive, so this
    /// asserts both branches — insert-then-update — actually run on T-SQL.
    /// </summary>
    [Fact]
    public async Task Upsert_InsertsThenUpdates_WithoutDuplicating()
    {
        var store = new DapperSettingStore(await NewConnectionAsync());

        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "first", Type = "System.String" });
        await store.UpsertAsync(new Setting { Group = "Mail", Key = "Host", Value = "second", Type = "System.String" });

        Assert.Equal("second", (await store.GetAsync("Mail", "Host"))!.Value);
        Assert.Equal(1, await store.CountAsync("Mail"));
    }

    [Fact]
    public async Task UpsertMany_IsTransactional_AcrossTheBatch()
    {
        var store = new DapperSettingStore(await NewConnectionAsync());

        await store.UpsertManyAsync(
        [
            new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" },
            new Setting { Group = "Mail", Key = "Port", Value = "587",  Type = "System.Int32"  },
        ]);

        Assert.Equal(2, await store.CountAsync("Mail"));
    }

    [Fact]
    public async Task UniqueIndex_RejectsADuplicateGroupKeyPair()
    {
        var connection = await NewConnectionAsync();

        await new DapperSettingStore(connection).UpsertAsync(
            new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" });

        // Bypasses the store's upsert to prove UX_Settings_Group_Key is really enforced.
        var ex = await Record.ExceptionAsync(() => connection.ExecuteAsync(
            """
            INSERT INTO Settings (Id,SettingGroup,SettingKey,Value,Type,IsEncrypted,UpdatedAt,UpdatedBy)
            VALUES (@Id,'Mail','Host','other','System.String',0,SYSUTCDATETIME(),NULL)
            """, new { Id = Guid.NewGuid() }));

        Assert.IsType<SqlException>(ex);
    }

    [Fact]
    public async Task Data_Survives_A_New_Connection_To_The_Same_Database()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var connection       = await OpenAsync(connectionString);

        await new DapperSettingStore(connection).UpsertAsync(
            new Setting { Group = "Mail", Key = "Host", Value = "smtp.live", Type = "System.String" });

        await using var fresh = new SqlConnection(connectionString);
        await fresh.OpenAsync();

        var reread = await new DapperSettingStore(fresh).GetAsync("Mail", "Host");
        Assert.Equal("smtp.live", reread!.Value);
    }

    [Fact]
    public async Task GuidTypeHandlers_RoundTripTheIdColumn()
    {
        var store   = new DapperSettingStore(await NewConnectionAsync());
        var setting = new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" };

        await store.UpsertAsync(setting);

        var reread = await store.GetAsync("Mail", "Host");
        Assert.Equal(setting.Id, reread!.Id);
        Assert.NotEqual(Guid.Empty, reread.Id);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
    }
}
