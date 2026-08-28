using Dapper;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Data;

namespace DotNetBoost.Settings.Dapper;

/// <summary>
/// Maps <see cref="Guid"/> to and from providers that store it as text, such as SQLite.
/// </summary>
public sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid value) => parameter.Value = value.ToString();
    /// <inheritdoc/>
    public override Guid Parse(object value) => value switch { Guid g => g, string s => Guid.Parse(s), _ => Guid.Parse(value.ToString()!) };
}

/// <summary>Nullable counterpart to <see cref="GuidTypeHandler"/>.</summary>
public sealed class NullableGuidTypeHandler : SqlMapper.TypeHandler<Guid?>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid? value) => parameter.Value = value?.ToString();
    /// <inheritdoc/>
    public override Guid? Parse(object value)
    {
        if (value is null || value is DBNull) return null;
        return value switch { Guid g => g, string s => Guid.Parse(s), _ => Guid.Parse(value.ToString()!) };
    }
}

internal static class DapperSchemaInitializer
{
    public static Task InitializeAsync(IDbConnection conn, CancellationToken ct = default)
    {
        var name = conn.GetType().Name.ToLowerInvariant();

        // Order matters: "npgsqlconnection" *contains* "sqlconnection", so Npgsql has to be
        // matched before SQL Server.
        var sql  = name switch
        {
            var x when x.Contains("npgsql")        => PostgreSqlScript,
            var x when x.Contains("sqlite")        => SqliteScript,
            var x when x.Contains("sqlconnection") => SqlServerScript,
            _ => throw new NotSupportedException($"Unsupported connection type: {conn.GetType().Name}")
        };
        return conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    private const string SqlServerScript = """
        IF OBJECT_ID('Settings','U') IS NULL
        BEGIN
            CREATE TABLE Settings (
                Id           UNIQUEIDENTIFIER PRIMARY KEY,
                SettingGroup NVARCHAR(191)    NOT NULL,
                SettingKey   NVARCHAR(191)    NOT NULL,
                Value        NVARCHAR(MAX)    NOT NULL,
                Type         NVARCHAR(500)    NOT NULL,
                IsEncrypted  BIT              NOT NULL DEFAULT 0,
                UpdatedAt    DATETIME2        NOT NULL,
                UpdatedBy    NVARCHAR(256)    NULL,
                RowVersion   VARBINARY(16)    NULL
            );
            CREATE UNIQUE INDEX UX_Settings_Group_Key ON Settings(SettingGroup, SettingKey);
        END;
        IF OBJECT_ID('SettingAudits','U') IS NULL
        BEGIN
            CREATE TABLE SettingAudits (
                Id           UNIQUEIDENTIFIER PRIMARY KEY,
                SettingGroup NVARCHAR(191)    NOT NULL,
                SettingKey   NVARCHAR(191)    NOT NULL,
                OldValue     NVARCHAR(MAX)    NOT NULL,
                NewValue     NVARCHAR(MAX)    NOT NULL,
                ChangedBy    NVARCHAR(256)    NOT NULL,
                ChangedAt    DATETIME2        NOT NULL
            );
            CREATE INDEX IX_SettingAudits_Group_Key ON SettingAudits(SettingGroup, SettingKey);
        END;
        """;

    private const string PostgreSqlScript = """
        CREATE TABLE IF NOT EXISTS Settings (
            Id           uuid          PRIMARY KEY,
            SettingGroup varchar(191)  NOT NULL,
            SettingKey   varchar(191)  NOT NULL,
            Value        text          NOT NULL,
            Type         varchar(500)  NOT NULL,
            IsEncrypted  boolean       NOT NULL DEFAULT false,
            UpdatedAt    timestamp     NOT NULL,
            UpdatedBy    varchar(256),
            RowVersion   bytea
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Settings_Group_Key ON Settings(SettingGroup,SettingKey);
        CREATE TABLE IF NOT EXISTS SettingAudits (
            Id           uuid          PRIMARY KEY,
            SettingGroup varchar(191)  NOT NULL,
            SettingKey   varchar(191)  NOT NULL,
            OldValue     text          NOT NULL,
            NewValue     text          NOT NULL,
            ChangedBy    varchar(256)  NOT NULL,
            ChangedAt    timestamp     NOT NULL
        );
        """;

    private const string SqliteScript = """
        CREATE TABLE IF NOT EXISTS Settings (
            Id           TEXT    PRIMARY KEY,
            SettingGroup TEXT    NOT NULL,
            SettingKey   TEXT    NOT NULL,
            Value        TEXT    NOT NULL,
            Type         TEXT    NOT NULL,
            IsEncrypted  INTEGER NOT NULL DEFAULT 0,
            UpdatedAt    TEXT    NOT NULL,
            UpdatedBy    TEXT,
            RowVersion   BLOB
        );
        CREATE UNIQUE INDEX IF NOT EXISTS UX_Settings_Group_Key ON Settings(SettingGroup, SettingKey);
        CREATE TABLE IF NOT EXISTS SettingAudits (
            Id           TEXT NOT NULL PRIMARY KEY,
            SettingGroup TEXT NOT NULL,
            SettingKey   TEXT NOT NULL,
            OldValue     TEXT NOT NULL,
            NewValue     TEXT NOT NULL,
            ChangedBy    TEXT NOT NULL,
            ChangedAt    TEXT NOT NULL
        );
        """;
}

/// <summary>
/// Row shape for the Settings table. Its property names mirror the column names, which keeps
/// the SELECT free of quoted aliases — <c>Group</c> and <c>Key</c> are reserved words, and the
/// quoting style for them differs per engine ([] on SQL Server, "" on PostgreSQL).
/// </summary>
internal sealed class SettingRow
{
    public Guid     Id           { get; set; }
    public string   SettingGroup { get; set; } = default!;
    public string   SettingKey   { get; set; } = default!;
    public string   Value        { get; set; } = default!;
    public string   Type         { get; set; } = default!;
    public bool     IsEncrypted  { get; set; }
    public DateTime UpdatedAt    { get; set; }
    public string?  UpdatedBy    { get; set; }
    public byte[]?  RowVersion   { get; set; }

    public Setting ToSetting() => new()
    {
        Id          = Id,
        Group       = SettingGroup,
        Key         = SettingKey,
        Value       = Value,
        Type        = Type,
        IsEncrypted = IsEncrypted,
        UpdatedAt   = UpdatedAt,
        UpdatedBy   = UpdatedBy,
        RowVersion  = RowVersion
    };
}

/// <summary>
/// Dapper-backed <see cref="ISettingStore"/> for SQL Server, PostgreSQL and SQLite.
/// </summary>
/// <param name="connection">
/// The connection to issue commands on. Its runtime type selects the SQL dialect used by
/// <c>migrateSchema</c>.
/// </param>
public sealed class DapperSettingStore(IDbConnection connection) : ISettingStore
{
    private static int _typeHandlersRegistered;

    static DapperSettingStore() => RegisterTypeHandlers();

    /// <summary>
    /// Registers the Guid type handlers this store depends on. Providers such as SQLite
    /// return <c>Id</c> as TEXT, which Dapper cannot map to <see cref="Guid"/> unaided.
    /// Runs automatically before the first store instance is created; idempotent.
    /// </summary>
    public static void RegisterTypeHandlers()
    {
        if (Interlocked.Exchange(ref _typeHandlersRegistered, 1) != 0) return;

        SqlMapper.AddTypeHandler(new NullableGuidTypeHandler());
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        const string sql = """
            SELECT Id, SettingGroup, SettingKey, Value, Type, IsEncrypted, UpdatedAt, UpdatedBy, RowVersion
            FROM Settings WHERE SettingGroup = @Group
            """;
        var rows = await connection.QueryAsync<SettingRow>(new CommandDefinition(sql, new { Group = group }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(static r => r.ToSetting()).ToList();
    }

    /// <inheritdoc/>
    public async Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        const string sql = """
            SELECT Id, SettingGroup, SettingKey, Value, Type, IsEncrypted, UpdatedAt, UpdatedBy, RowVersion
            FROM Settings WHERE SettingGroup = @Group AND SettingKey = @Key
            """;
        var row = await connection.QueryFirstOrDefaultAsync<SettingRow>(new CommandDefinition(sql, new { Group = group, Key = key }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToSetting();
    }

    /// <inheritdoc/>
    public Task UpsertAsync(Setting setting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return UpsertInternalAsync(setting, null, ct);
    }


    /// <inheritdoc/>
    public async Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings as IList<Setting> ?? settings.ToList();
        if (list.Count == 0) return;

        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) connection.Open();
        using var tx = connection.BeginTransaction();
        try
        {
            foreach (var item in list)
                await UpsertInternalAsync(item, tx, ct).ConfigureAwait(false);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        finally { if (shouldClose) connection.Close(); }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        const string sql = "DELETE FROM Settings WHERE SettingGroup = @Group AND SettingKey = @Key";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Group = group, Key = key }, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        const string sql = "DELETE FROM Settings WHERE SettingGroup = @Group";
        await connection.ExecuteAsync(new CommandDefinition(sql, new { Group = group }, cancellationToken: ct)).ConfigureAwait(false);
    }


    /// <inheritdoc/>
    public Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        const string sql = "SELECT COUNT(*) FROM Settings WHERE SettingGroup = @Group";
        return connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Group = group }, cancellationToken: ct));
    }

    // Deliberately two round-trips rather than one batch. The affected-row counts are what
    // separate the three outcomes — updated, inserted, or beaten by a concurrent writer — and
    // a multi-statement batch reports those inconsistently across providers.
    private const string UpdateSql = """
        UPDATE Settings SET Value=@Value, Type=@Type, IsEncrypted=@IsEncrypted,
            UpdatedAt=@UpdatedAt, UpdatedBy=@UpdatedBy, RowVersion=@NewRowVersion
        WHERE SettingGroup=@SettingGroup AND SettingKey=@SettingKey
          AND (@ExpectedRowVersion IS NULL OR RowVersion = @ExpectedRowVersion)
        """;

    private const string InsertIfAbsentSql = """
        INSERT INTO Settings (Id,SettingGroup,SettingKey,Value,Type,IsEncrypted,UpdatedAt,UpdatedBy,RowVersion)
        SELECT @Id,@SettingGroup,@SettingKey,@Value,@Type,@IsEncrypted,@UpdatedAt,@UpdatedBy,@NewRowVersion
        WHERE NOT EXISTS (SELECT 1 FROM Settings WHERE SettingGroup=@SettingGroup AND SettingKey=@SettingKey)
        """;

    private async Task UpsertInternalAsync(Setting s, IDbTransaction? tx, CancellationToken ct)
    {
        var parameters = new
        {
            s.Id,
            SettingGroup = s.Group,
            SettingKey   = s.Key,
            s.Value,
            s.Type,
            s.IsEncrypted,
            s.UpdatedAt,
            s.UpdatedBy,
            ExpectedRowVersion = s.RowVersion,
            NewRowVersion      = Setting.NewRowVersion()
        };

        var updated = await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, parameters, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (updated > 0) return;

        var inserted = await connection.ExecuteAsync(
            new CommandDefinition(InsertIfAbsentSql, parameters, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (inserted > 0) return;

        // Nothing updated and nothing inserted: the row is there, but not with the token the
        // caller expected.
        throw new SettingConcurrencyException(s.Group, s.Key);
    }
}

internal sealed class DapperMigrationHostedService(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var conn = scope.ServiceProvider.GetRequiredService<IDbConnection>();
        await DapperSchemaInitializer.InitializeAsync(conn, ct).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
