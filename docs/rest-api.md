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
| `GET`  | `/api/settings` | The registered groups — see [Discovery](discovery-endpoints.md) |
| `GET`  | `/api/settings/{route}/schema` | One group's properties, types, defaults and constraints |

All of them carry the OpenAPI tag `Settings`, so they group themselves in Swagger or Scalar
without being wrapped in a `MapGroup("")` purely to hang a tag on.

The last two are the discovery pair, and neither returns a stored value. They are on by default;
`MapSettingsEndpoints(requireIfMatch: false, includeDiscovery: false)` leaves them out.

> There is no `/audit` endpoint. Auditing is not built in — see
> [Write notifications](write-notifications.md) for the hook that replaced it.

## Reshaping what GET returns

`GET` serves the stored values. When the representation your UI needs is not the representation
you store — a display name in the request's language, an image id resolved to a URL, a field
masked for display — register an `ISettingProjector<T>`:

```csharp
public sealed class BrandingProjector(ITranslator translator) : ISettingProjector<BrandingSettings>
{
    public async Task<object> ProjectAsync(BrandingSettings group, CancellationToken ct = default)
        => new
        {
            StoreName = await translator.ForCurrentRequestAsync(group.StoreName, ct),
            group.PrimaryColor
        };
}
```

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseProjector<BrandingSettings, BrandingProjector>()
    .Build();
```

Projectors are registered scoped, so they can depend on anything else in the request scope.
Four rules make this safe to have:

- **Read path only.** `POST` never consults a projector, so it cannot influence what is stored.
  The projected shape is an output shape; it is not something you can post back.
- **Never applied to the `ETag`.** The tag keeps describing the stored revision. A tag derived
  from projected output would move with the request's language, and `If-Match` would stop
  protecting anything.
- **No projector registered is the previous behaviour, byte for byte.**
- **A projector that throws fails the request** — deliberately unlike
  [`ISettingChangedHandler<T>` and `ISettingWriteObserver`](write-notifications.md#failure-policy),
  whose exceptions are logged and swallowed. A failed notification must not break a write that
  has already committed; a failed projection means the response would be wrong, and a wrong
  response must not be hidden.

Programmatic reads are unaffected: `For<T>().GetAsync()` always returns the stored group.
Note that the OpenAPI schema for `GET` still describes `T` — the document cannot know what your
projector returns, so describe the projected shape yourself if the generated document matters.

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

