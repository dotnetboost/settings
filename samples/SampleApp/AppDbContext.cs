using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SampleApp.Settings;

namespace SampleApp;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // The entire settings registration. No interface and no DbSet properties: the store
    // reaches its entities through Set<T>(), and the engine — which decides the column type
    // chosen for Value and how RowVersion is mapped — is read off the context, so switching
    // the provider block in Program.cs needs no matching edit here.
    //
    // To pin the engine instead (a model built against one and migrated onto another):
    // => modelBuilder.ApplySettings(DatabaseProvider.PostgreSql);
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettings(this);
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
