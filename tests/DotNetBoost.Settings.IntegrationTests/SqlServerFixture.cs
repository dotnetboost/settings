using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace DotNetBoost.Settings.IntegrationTests;

/// <summary>
/// Starts a single SQL Server container for the whole collection. Each call to
/// <see cref="CreateDatabaseAsync"/> hands out a brand-new database on that container,
/// so every test gets a genuinely empty store without paying to boot a container.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private int _databaseCounter;

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns a connection string pointing at it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // SQL Server identifiers cap at 128 characters; this is 39-41, so no truncation needed.
        var name = $"test_{Interlocked.Increment(ref _databaseCounter)}_{Guid.NewGuid():N}";

        await using (var admin = new SqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            // Database names cannot be parameterised; the name is generated here, never user input.
            cmd.CommandText = $"CREATE DATABASE [{name}]";
            await cmd.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = name
        }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
