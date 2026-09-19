# Audit trail

When an audit store is registered, `SetAsync` records the before/after value, property key, and timestamp for **each property whose value actually changed**. Properties identical to what is already stored are not written to the trail, so saving a model with one edited field produces one entry rather than one per property.

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAuditStore<EfCoreAuditStore>()      // ships with the EF Core package
    .Build();
```

Query history directly:

```csharp
IReadOnlyList<SettingAuditEntry> history =
    await auditStore.GetHistoryAsync("MailSettings", key: "Host");
```

Or via the REST API — every settings group automatically gets a `GET /api/settings/{route}/audit` endpoint. The audit store is optional: without one the endpoint returns `404` with an explanatory body, and the rest of the settings API is unaffected.

Values marked `[Sensitive]` are recorded as `[encrypted]` on both sides of the entry rather than in cleartext. Change detection compares them as plaintext, not as stored ciphertext: AES-GCM draws a fresh nonce on every call, so an unchanged secret is re-encrypted to different bytes each save and a ciphertext comparison would log a spurious change every time.

Write your own store (SQL table, Elasticsearch, whatever) by implementing `ISettingAuditStore`.

