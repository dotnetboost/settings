using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Dapper;
using Microsoft.Data.Sqlite;

namespace DotNetBoost.Settings.ProviderTests.Stores;

public sealed class DapperSettingStoreTests : SettingStoreContractTests, IDisposable
{
    private readonly List<SqliteConnection> _connections = [];

    protected override async Task<ISettingStore> CreateStoreAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        _connections.Add(connection);

        // The shipped DDL, not a copy of it. A hand-maintained duplicate here silently drifts
        // from the real schema — which is exactly what happened when RowVersion was added.
        await DapperSchemaInitializer.InitializeAsync(connection);

        return new DapperSettingStore(connection);
    }

    public void Dispose()
    {
        foreach (var c in _connections) c.Dispose();
    }
}
