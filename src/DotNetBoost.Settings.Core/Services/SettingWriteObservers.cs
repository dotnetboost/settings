using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace DotNetBoost.Settings.Core.Services;

/// <summary>
/// Default <see cref="ISettingActorAccessor"/>: reports no actor. Registered by
/// <c>AddSettings()</c> so the service always resolves.
/// </summary>
internal sealed class NullSettingActorAccessor : ISettingActorAccessor
{
    public string? GetActor() => null;
}

/// <summary>
/// An <see cref="ISettingWriteObserver"/> that writes one log line per settings write.
/// Opt in with <c>.UseLoggingWriteObserver()</c>; nothing logs writes by default.
/// </summary>
/// <remarks>
/// <b>Property names only — never values, redacted or not.</b> A settings value is exactly
/// the kind of thing that should not reach a log aggregator because a default turned it on:
/// a connection string, a webhook URL, an account identifier. Which properties changed, who
/// changed them and under what correlation id is the part worth having by default, and it is
/// also the part that is safe to keep.
/// <para>
/// This exists so the write hook is not an empty port. For a real trail — queryable, with
/// retention — implement <see cref="ISettingWriteObserver"/> over your own store.
/// </para>
/// </remarks>
public sealed partial class LoggingSettingWriteObserver : ISettingWriteObserver
{
    private readonly ILogger<LoggingSettingWriteObserver> _logger;

    /// <summary>Creates the observer.</summary>
    public LoggingSettingWriteObserver(ILogger<LoggingSettingWriteObserver> logger)
        => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Joining the property names allocates, so do not do it for a disabled level.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var actor      = write.Actor ?? "(not captured)";
            var properties = write.Changes.Count == 0
                ? "(none)"
                : string.Join(", ", write.Changes.Select(Describe));

            LogWrite(_logger, write.GroupName, write.Kind, actor, write.CorrelationId, properties);
        }

        return Task.CompletedTask;
    }

    private static string Describe(SettingChange change)
        => change.IsRedacted
            ? string.Create(CultureInfo.InvariantCulture, $"{change.PropertyName} [redacted]")
            : change.PropertyName;

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "Settings group '{group}' {kind} by {actor} (correlation {correlationId}); properties: {properties}.")]
    private static partial void LogWrite(
        ILogger logger, string group, SettingWriteKind kind, string actor, string correlationId, string properties);
}
