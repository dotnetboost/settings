# Encrypting sensitive values

Mark any property `[Sensitive]` and it is transparently encrypted before storage and decrypted on read — API keys, passwords, connection strings never touch the database as plaintext.

This is encryption **at rest**. It protects a database dump, a backup, or anyone with table access. It is not an access control: your application reads these values decrypted, and so does the REST API if you expose it — see [Securing the endpoints](rest-api.md#securing-the-endpoints).

```csharp
[SettingGroup("mail-server")]
public class MailSettings
{
    public string Host { get; set; } = "smtp.example.com";

    [Sensitive]
    public string Password { get; set; } = string.Empty;
}
```

```csharp
// Built-in AES-256-GCM encryptor
var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); // store this in a secret manager!

builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAesEncryption(key)
    .Build();
```

Or plug in your own (Azure Key Vault, AWS KMS, etc.):

```csharp
public class KeyVaultEncryptor(SecretClient client) : ISettingEncryptor
{
    public string Encrypt(string plaintext) { /* ... */ }
    public string Decrypt(string ciphertext) { /* ... */ }
}

builder.Services.AddSettings()
    .UseCustomEncryption<KeyVaultEncryptor>()
    .Build();
```

> **Never hardcode the AES key.** Load it from an environment variable, Azure Key Vault, AWS Secrets Manager, or similar — the sample app generates a throwaway key at startup purely for demonstration.

## Rotating the encryption key

Each encrypted value is stored as `v1:{keyId}:{base64}`, where `keyId` is a short fingerprint of the key that wrote it. That is what makes rotation safe: a value can be traced back to its key instead of just failing to authenticate.

Pass the new key first and keep the old one as a retired key — retired keys decrypt, they never encrypt:

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .UseAesEncryption(newKey, oldKey)   // reads use either; writes use newKey
    .Build();
```

Deploy that, then rewrite each group once (any `SetAsync` will do — including a save from the dashboard or `POST /api/settings/{route}`). Every rewritten group is re-encrypted under the new key. Once all of them have been rewritten, drop `oldKey`:

```csharp
    .UseAesEncryption(newKey)
```

Values written before key ids existed are still readable: they carry no `v1:` prefix, so each configured key is tried in turn. AES-GCM authenticates, so a wrong key fails cleanly rather than returning garbage.

> **A value that cannot be decrypted throws `SettingDecryptionException`.** This is deliberate — the alternative is a settings model whose secrets silently hold their compile-time defaults, so a mishandled rotation would leave the application running on default credentials instead of failing. `IgnoreDecryptionFailures()` restores the fall-back-to-default behaviour if you genuinely want it.

