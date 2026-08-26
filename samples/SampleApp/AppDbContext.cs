using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SampleApp.Settings;

namespace SampleApp;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), ISettingDbContext
{
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    // Must match the provider block active in Program.cs: it decides the column type
    // chosen for Value (text / nvarchar(max) / longtext) and how RowVersion is mapped.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.PostgreSql);
        // => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.SqlServer);
        // => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.Sqlite);
}

/// <summary>
/// Demonstrates the change-notification feature: reconfigures a live SmtpClient
/// pool whenever MailSettings are updated at runtime, with no redeploy needed.
/// </summary>
public sealed partial class MailSettingsChangedHandler(ILogger<MailSettingsChangedHandler> logger)
    : ISettingChangedHandler<MailSettings>
{
    public Task OnChangedAsync(MailSettings previous, MailSettings current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        LogMailSettingsChanged(logger, previous.Host, previous.Port, current.Host, current.Port);

        // In a real app: rebuild your SmtpClient / connection pool here.
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Mail settings changed: {oldHost}:{oldPort} -> {newHost}:{newPort}")]
    private static partial void LogMailSettingsChanged(
        ILogger logger, string? oldHost, int oldPort, string? newHost, int newPort);
}
