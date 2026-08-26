using Dapper;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Dapper;
using DotNetBoost.Settings.ProviderTests.Stores;
using Npgsql;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

/// <summary>
/// Runs the full <see cref="SettingStoreContractTests"/> suite against the Dapper store on a
/// real PostgreSQL server. This is the only coverage of the PostgreSql branch of
/// <c>DapperSchemaInitializer</c> and of the Postgres-flavoured upsert SQL.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DapperPostgresStoreTests(PostgreSqlFixture fixture)
    : SettingStoreContractTests, IAsyncLifetime
{
    private readonly List<NpgsqlConnection> _connections = [];

    protected override async Task<ISettingStore> CreateStoreAsync()
        => new DapperSettingStore(await NewConnectionAsync());

    private async Task<NpgsqlConnection> NewConnectionAsync()
        => await OpenAsync(await fixture.CreateDatabaseAsync());

    private async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        _connections.Add(connection);

        // Exercises the real PostgreSql DDL shipped with the provider.
        await DapperSchemaInitializer.InitializeAsync(connection);
        return connection;
    }

    [Fact]
    public async Task SchemaInitializer_CreatesBothTables_AndIsIdempotent()
    {
        var connection = await NewConnectionAsync();

        // Running it a second time must not throw: the hosted service calls it on every boot.
        await DapperSchemaInitializer.InitializeAsync(connection);

        var tables = await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'");

        Assert.Contains("settings", tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("settingaudits", tables, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Data_Survives_A_New_Connection_To_The_Same_Database()
    {
        // Held separately: Npgsql strips the password from ConnectionString once connected.
        var connectionString = await fixture.CreateDatabaseAsync();
        var connection       = await OpenAsync(connectionString);

        await new DapperSettingStore(connection).UpsertAsync(
            new Setting { Group = "Mail", Key = "Host", Value = "smtp.live", Type = "System.String" });

        await using var fresh = new NpgsqlConnection(connectionString);
        await fresh.OpenAsync();

        var reread = await new DapperSettingStore(fresh).GetAsync("Mail", "Host");
        Assert.Equal("smtp.live", reread!.Value);
    }

    [Fact]
    public async Task GuidTypeHandlers_RoundTripTheIdColumn()
    {
        var connection = await NewConnectionAsync();
        var store      = new DapperSettingStore(connection);
        var setting    = new Setting { Group = "Mail", Key = "Host", Value = "smtp", Type = "System.String" };

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
