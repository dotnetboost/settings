using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text;

namespace DotNetBoost.Settings.UnitTests;

public class AesKeyRotationTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Ciphertext_CarriesAKeyId()
    {
        var enc = new AesSettingEncryptor(NewKey());

        var cipher = enc.Encrypt("secret");

        Assert.StartsWith($"v1:{enc.PrimaryKeyId}:", cipher, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyId_IsStableForTheSameKey_AndDiffersBetweenKeys()
    {
        var key = NewKey();

        Assert.Equal(new AesSettingEncryptor(key).PrimaryKeyId, new AesSettingEncryptor(key).PrimaryKeyId);
        Assert.NotEqual(new AesSettingEncryptor(key).PrimaryKeyId, new AesSettingEncryptor(NewKey()).PrimaryKeyId);
    }

    [Fact]
    public void KeyId_IsNotABareHashOfTheKey()
    {
        // Domain-separated, so the stored id is not something an attacker can match against a
        // precomputed hash of a candidate key.
        var key   = NewKey();
        var bare  = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(key)))[..8].ToLowerInvariant();

        Assert.NotEqual(bare, new AesSettingEncryptor(key).PrimaryKeyId);
    }

    [Fact]
    public void RotatedKey_StillDecryptsValuesWrittenUnderTheOldKey()
    {
        var oldKey = NewKey();
        var newKey = NewKey();

        var before = new AesSettingEncryptor(oldKey).Encrypt("api-key-v1");

        // The rotated deployment: new key primary, old key retained for reads.
        var after = new AesSettingEncryptor(newKey, oldKey);

        Assert.Equal("api-key-v1", after.Decrypt(before));
    }

    [Fact]
    public void RotatedKey_WritesUnderTheNewKey()
    {
        var oldKey = NewKey();
        var newKey = NewKey();
        var rotated = new AesSettingEncryptor(newKey, oldKey);

        var cipher = rotated.Encrypt("fresh");

        Assert.StartsWith($"v1:{new AesSettingEncryptor(newKey).PrimaryKeyId}:", cipher, StringComparison.Ordinal);
        // Once rewritten, the retired key is no longer needed to read it.
        Assert.Equal("fresh", new AesSettingEncryptor(newKey).Decrypt(cipher));
    }

    [Fact]
    public void DroppingTheOldKey_FailsWithAMessageNamingTheMissingKey()
    {
        var oldKey = NewKey();
        var before = new AesSettingEncryptor(oldKey).Encrypt("api-key-v1");
        var missingId = new AesSettingEncryptor(oldKey).PrimaryKeyId;

        var ex = Assert.ThrowsAny<CryptographicException>(
            () => new AesSettingEncryptor(NewKey()).Decrypt(before));

        Assert.Contains(missingId, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupplyingTheSameKeyTwice_Throws()
    {
        // Otherwise the rotation looks configured but the retired list is a no-op.
        var key = NewKey();
        Assert.Throws<ArgumentException>(() => new AesSettingEncryptor(key, key));
    }

    [Fact]
    public void LegacyCiphertext_WithoutAKeyId_StillDecrypts()
    {
        var key = NewKey();

        // Exactly what the pre-rotation encryptor wrote: bare Base64 of nonce||ct||tag.
        var legacy = LegacyEncrypt(key, "written-before-key-ids");

        Assert.Equal("written-before-key-ids", new AesSettingEncryptor(key).Decrypt(legacy));
    }

    [Fact]
    public void LegacyCiphertext_IsFoundAmongRetiredKeys()
    {
        var oldKey = NewKey();
        var legacy = LegacyEncrypt(oldKey, "old-value");

        Assert.Equal("old-value", new AesSettingEncryptor(NewKey(), oldKey).Decrypt(legacy));
    }

    [Fact]
    public void LegacyCiphertext_WithNoMatchingKey_Throws()
        => Assert.ThrowsAny<CryptographicException>(
            () => new AesSettingEncryptor(NewKey()).Decrypt(LegacyEncrypt(NewKey(), "x")));

    [Fact]
    public void MalformedValue_ThrowsCryptographicException_NotFormatException()
        => Assert.ThrowsAny<CryptographicException>(() => new AesSettingEncryptor(NewKey()).Decrypt("not base64!!"));

    [Fact]
    public void TamperedCiphertext_IsRejected()
    {
        var enc    = new AesSettingEncryptor(NewKey());
        var cipher = enc.Encrypt("secret");
        var parts  = cipher.Split(':');
        var raw    = Convert.FromBase64String(parts[2]);
        raw[^1] ^= 0xFF;   // flip a bit in the auth tag

        var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(raw)}";

        Assert.ThrowsAny<CryptographicException>(() => enc.Decrypt(tampered));
    }

    /// <summary>Reproduces the original envelope: base64(nonce || ciphertext || tag), no prefix.</summary>
    private static string LegacyEncrypt(string base64Key, string plaintext)
    {
        var key        = Convert.FromBase64String(base64Key);
        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plain      = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plain.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, nonce.Length);
        tag.CopyTo(result, nonce.Length + ciphertext.Length);
        return Convert.ToBase64String(result);
    }
}

/// <summary>
/// A value that cannot be decrypted must not silently degrade to the property's default —
/// that turns a botched key rotation into an application running on default credentials.
/// </summary>
public class DecryptionFailureTests
{
    public class SecretSettings
    {
        public string? Host { get; set; }
        [Sensitive] public string ApiKey { get; set; } = "DEFAULT-DO-NOT-USE";
    }

    [Fact]
    public async Task UndecryptableValue_ThrowsByDefault()
    {
        var mgr = Build(out _);

        var ex = await Assert.ThrowsAsync<SettingDecryptionException>(
            () => mgr.For<SecretSettings>().GetAsync());

        Assert.Equal("SecretSettings", ex.Group);
        Assert.Equal(nameof(SecretSettings.ApiKey), ex.Key);
    }

    [Fact]
    public async Task UndecryptableValue_FallsBackToDefault_WhenExplicitlyIgnored()
    {
        var mgr = Build(out _, o => o.ThrowOnDecryptionFailure = false);

        var model = await mgr.For<SecretSettings>().GetAsync();

        Assert.Equal("DEFAULT-DO-NOT-USE", model.ApiKey);
        Assert.Equal("smtp.example.com", model.Host);   // unencrypted properties still load
    }

    [Fact]
    public void IgnoreDecryptionFailures_FlowsFromTheBuilder()
    {
        var services = new ServiceCollection();
        services.AddSettings().IgnoreDecryptionFailures();

        Assert.False(services.BuildServiceProvider().GetRequiredService<SettingOptions>().ThrowOnDecryptionFailure);
    }

    [Fact]
    public void ThrowOnDecryptionFailure_DefaultsToTrue()
        => Assert.True(new SettingOptions().ThrowOnDecryptionFailure);

    // A store holding a value encrypted under a key the manager no longer has.
    private static SettingManager Build(out Mock<ISettingStore> store, Action<SettingOptions>? configure = null)
    {
        var rotatedAwayKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var stranded       = new AesSettingEncryptor(rotatedAwayKey).Encrypt("real-secret");

        store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync("SecretSettings", It.IsAny<CancellationToken>()))
             .ReturnsAsync(
             [
                 new Setting { Group = "SecretSettings", Key = "Host",   Value = "smtp.example.com", Type = "System.String" },
                 new Setting { Group = "SecretSettings", Key = "ApiKey", Value = stranded, Type = "System.String", IsEncrypted = true },
             ]);

        var cache = new Mock<ISettingCache>();
        SecretSettings? miss = null;
        cache.Setup(x => x.TryGetValue<SecretSettings>(It.IsAny<string>(), out miss)).Returns(false);

        var options = new SettingOptions();
        configure?.Invoke(options);

        return new SettingManager(store.Object, cache.Object,
            new ServiceCollection().BuildServiceProvider(), NullLogger<SettingManager>.Instance,
            new AesSettingEncryptor(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            null, options);
    }
}
