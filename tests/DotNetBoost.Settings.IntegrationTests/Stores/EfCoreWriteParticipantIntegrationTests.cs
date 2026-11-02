using DotNetBoost.Settings.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

/// <summary>
/// The participant suite on a real PostgreSQL server, where a transaction and a concurrency
/// conflict are the server's, not SQLite's.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCorePostgresWriteParticipantTests(PostgreSqlFixture fixture) : EfCoreWriteParticipantTests
{
    protected override async Task<Action<DbContextOptionsBuilder>> NewDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        return o => o.UseNpgsql(connectionString);
    }
}

/// <summary>
/// The participant suite on a real SQL Server, under a retrying execution strategy. A clear
/// that opened its transaction outside the strategy would throw here on every call, which is
/// why the store opens it inside.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCoreSqlServerWriteParticipantTests(SqlServerFixture fixture) : EfCoreWriteParticipantTests
{
    protected override async Task<Action<DbContextOptionsBuilder>> NewDatabaseAsync()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        return o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    }
}
