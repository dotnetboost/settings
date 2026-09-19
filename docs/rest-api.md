# REST API endpoints

```bash
dotnet add package DotNetBoost.Settings.API --prerelease
```

```csharp
app.MapSettingsEndpoints();
```

Registers, per `[SettingGroup]` class:

| Method | Route | Description |
|---|---|---|
| `GET`  | `/api/settings/{route}` | Current values, with an `ETag` for the revision |
| `POST` | `/api/settings/{route}` | Validate + persist; honours `If-Match`, `412` on a lost race |
| `GET`  | `/api/settings/{route}/audit` | Change history (`404` if no audit store configured) |

## Securing the endpoints

**The generated endpoints are anonymous by default.** The library deliberately does not impose an
authorization policy — who may read and write your settings is your application's decision, not
this package's. It gives you the mechanism; you choose the policy.

Apply `[Authorize]` to the settings class and it covers all three endpoints for that group:

```csharp
[SettingGroup("payment", Name = "PaymentSettings")]
[Authorize(Roles = "Admin")]                    // or [Authorize(Policy = "SettingsAdmin")]
public class PaymentSettings
{
    [Sensitive]
    public string ApiKey { get; set; } = string.Empty;
}
```

> **Do this before exposing a group that holds `[Sensitive]` properties.**
> `GET` returns the settings object as your application sees it, which means secrets come back
> **decrypted** — that is what makes an editable admin UI possible. `[Sensitive]` encrypts values
> *at rest*: it protects a database dump, a backup, or a DBA with table access. It does not
> protect the API, which holds the key and decrypts on read. Without `[Authorize]`, a single
> unauthenticated `GET` returns every secret in the group in plaintext.

Standard ASP.NET Core authorization applies, so anything that works elsewhere works here —
roles, policies, schemes:

```csharp
[Authorize(AuthenticationSchemes = "Bearer", Policy = "SettingsAdmin")]
```

Remember that authorization is per class. Adding a new `[SettingGroup]` starts it anonymous, so
the attribute is worth adding at the same time as the class rather than afterwards.

> The [`samples/SampleApp`](../samples/SampleApp) settings classes carry no `[Authorize]` on
> purpose — the sample is a showcase of the library's features and runs without an identity
> provider. Do not copy that part into a real application.

