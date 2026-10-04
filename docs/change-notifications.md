# Change notifications

React to settings changes at runtime — no restart needed to pick up a new SMTP host, feature flag, or rate limit.

```csharp
public class MailSettingsChangedHandler(ILogger<MailSettingsChangedHandler> logger)
    : ISettingChangedHandler<MailSettings>
{
    public Task OnChangedAsync(MailSettings previous, MailSettings current, CancellationToken ct = default)
    {
        logger.LogInformation("Mail host changed: {Old} -> {New}", previous.Host, current.Host);
        // Rebuild your SmtpClient pool, refresh a cached connection, etc.
        return Task.CompletedTask;
    }
}
```

```csharp
builder.Services.AddScoped<MailSettingsChangedHandler>();
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .OnChanged<MailSettings, MailSettingsChangedHandler>()
    .Build();
```

Multiple handlers per settings type are supported and run in registration order. A handler that throws is logged and does not roll back the write or block other handlers.

## Which of the two hooks you want

The library notifies you of a write in two shapes. They serve different consumers, and picking
the wrong one is mostly a matter of fighting the data you are handed.

| | `ISettingChangedHandler<T>` | [`ISettingWriteObserver`](write-notifications.md) |
|---|---|---|
| Scope | One settings group, typed | Every group, untyped |
| Payload | The `previous` and `current` models | Per-property diff, plus actor and correlation id |
| Good for | Reacting to a new value: *if `MailSettings.Host` changed, reconnect* | Recording or forwarding what happened: auditing, webhooks, indexing |
| Reports `ClearAsync` | No | Yes, as `SettingWriteKind.Cleared` |

Both are **in-process only**, and both have the same failure policy: **an exception is logged and
swallowed**, so neither can fail a write that has already committed, and one failing handler
never skips the ones after it.

Registering both is normal — they are not alternatives.
