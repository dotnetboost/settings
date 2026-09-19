# Validation

There are two ways to validate settings. They differ in which write paths they cover:

| | Package | `POST` to the REST API | `SetAsync()` from your code |
|---|---|---|---|
| [FluentValidation](#fluentvalidation) | `DotNetBoost.Settings.FluentValidation` | ✅ | ✅ |
| [Data Annotations](#data-annotations) | none (built in) | ✅ | ❌ not checked |

Use FluentValidation if your own code writes settings and you want those writes validated too.

## FluentValidation

**1. Install the package**

```bash
dotnet add package DotNetBoost.Settings.FluentValidation --prerelease
```

It brings in `FluentValidation` itself, so you don't need to install that separately.

**2. Write a validator for your settings class**

```csharp
using FluentValidation;

public class MailSettingsValidator : AbstractValidator<MailSettings>
{
    public MailSettingsValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
    }
}
```

**3. Register it**

`UseFluentValidation` finds every validator in the assembly you pass:

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseFluentValidation(typeof(Program).Assembly)
    .Build();
```

When a value is invalid:

- `SetAsync()` throws `SettingValidationException`. Its `Errors` dictionary maps each property name to its messages. Nothing is written.
- `POST /api/settings/{route}` returns HTTP `400` with `ValidationProblemDetails`. Nothing is written.

## Data Annotations

No extra package is needed. Add the attributes to your settings class:

```csharp
using System.ComponentModel.DataAnnotations;

[SettingGroup("mail-server")]
public class MailSettings
{
    [Required, MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 587;
}
```

An invalid `POST /api/settings/{route}` returns HTTP `400` with `ValidationProblemDetails`.

> **Data Annotations are only checked by the REST API.** A `SetAsync()` call from your own code
> saves the value without checking these attributes. Also, if a FluentValidation validator exists
> for the same class, the API uses it and ignores the attributes.
