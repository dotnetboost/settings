# Defining settings

```csharp
[SettingGroup("payment", Name = "PaymentSettings")]
public class PaymentSettings
{
    public string GatewayUrl { get; set; } = "https://gateway.example.com";

    [Sensitive]                          // encrypted at rest
    public string ApiKey { get; set; } = string.Empty;

    [SettingDefault(10_000)]             // used when no row exists yet
    public decimal MaxAmount { get; set; } = 10_000m;

    public bool SandboxMode { get; set; } = true;
}
```

**Rules:**
- Group name must be unique application-wide (see [Group names](#group-names) below).
- `[SettingGroup]` route value must be unique and non-empty.
- Violations throw `InvalidOperationException` at `Build()` time — fail fast at startup, not at 2am in production.

## Group names

`[SettingGroup]` carries two independent identifiers, and it is worth knowing which is which:

| | What it controls | Safe to change? |
|---|---|---|
| `route` (positional) | The URL segment: `/api/settings/{route}` | Yes — it is a URL, no stored data depends on it |
| `Name` | The **storage key** every row for this group is written under | No — changing it strands the existing rows |

`Name` is optional and **defaults to the class name**, which is the historical behaviour. Adding the attribute or upgrading the package never moves existing data.

Setting it explicitly is recommended, because without it your database schema is silently coupled to a C# identifier:

```csharp
[SettingGroup("mail-server", Name = "MailSettings")]
public class MailSettings { /* ... */ }
```

With `Name` pinned, the class can be renamed, moved to another namespace, or reorganised freely and it keeps reading the same rows. Without it, a rename that looks like pure refactoring silently orphans every stored value and the application quietly comes back up on defaults — including default credentials.

### Migrating an existing group

Adding `Name` to a class that **already has rows** requires renaming those rows, because you are changing the key they are stored under. Do it in the same deploy as the code change:

```sql
UPDATE Settings     SET SettingGroup = 'new-name' WHERE SettingGroup = 'OldClassName';
UPDATE SettingAudits SET SettingGroup = 'new-name' WHERE SettingGroup = 'OldClassName';
```

```js
// MongoDB
db.settings.updateMany({ Group: "OldClassName" }, { $set: { Group: "new-name" } })
```

If you set `Name` to exactly the current class name — as the samples above do — there is nothing to migrate, and you gain the freedom to rename the class later.

Two groups resolving to the same name would read and write each other's rows, so `Build()` rejects it at startup. That check compares resolved names, which means two same-named classes in different namespaces still collide unless one of them sets a distinct `Name`.

## Default values

```csharp
[SettingGroup("rate-limit")]
public class RateLimitSettings
{
    [SettingDefault(100)]
    public int RequestsPerMinute { get; set; }
}
```

If no row exists in the store yet, `RequestsPerMinute` returns `100` instead of the CLR default `0` — useful for rolling out a new setting without a migration that back-fills every existing environment.

