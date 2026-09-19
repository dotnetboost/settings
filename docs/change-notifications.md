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

