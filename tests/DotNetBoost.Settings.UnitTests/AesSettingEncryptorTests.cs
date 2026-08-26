using DotNetBoost.Settings.Core.Services;
using System.Security.Cryptography;

namespace DotNetBoost.Settings.UnitTests;

public class AesSettingEncryptorTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Encrypt_Then_Decrypt_RoundTrips()
    {
        var encryptor = new AesSettingEncryptor(NewKey());
        var plaintext = "super-secret-api-key";

        var cipher    = encryptor.Encrypt(plaintext);
        var decrypted = encryptor.Decrypt(cipher);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertext_ForSameInput()
    {
        // Different nonce each time -> different ciphertext for the same plaintext.
        var encryptor = new AesSettingEncryptor(NewKey());
        var c1 = encryptor.Encrypt("value");
        var c2 = encryptor.Encrypt("value");
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void Encrypt_ProducesAVersionedEnvelope_WithABase64Payload()
    {
        // v1:{keyId}:{base64(nonce || ciphertext || tag)} — the key id is what makes a value
        // written under a retired key identifiable after a rotation.
        var encryptor = new AesSettingEncryptor(NewKey());

        var parts = encryptor.Encrypt("value").Split(':');

        Assert.Equal(3, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal(encryptor.PrimaryKeyId, parts[1]);
        Assert.Null(Record.Exception(() => Convert.FromBase64String(parts[2])));
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var encryptor1 = new AesSettingEncryptor(NewKey());
        var encryptor2 = new AesSettingEncryptor(NewKey());
        var cipher = encryptor1.Encrypt("secret");

        Assert.ThrowsAny<CryptographicException>(() => encryptor2.Decrypt(cipher));
    }

    [Fact]
    public void Constructor_InvalidKeyLength_Throws()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); // AES-128, not 256
        Assert.Throws<ArgumentException>(() => new AesSettingEncryptor(shortKey));
    }

    [Fact]
    public void Constructor_NullOrEmptyKey_Throws()
    {
        // Assert.Throws matches the exact type: null yields ArgumentNullException,
        // empty/whitespace yields ArgumentException.
        Assert.Throws<ArgumentNullException>(() => new AesSettingEncryptor(null!));
        Assert.Throws<ArgumentException>(() => new AesSettingEncryptor(""));
    }

    [Fact]
    public void Encrypt_EmptyString_RoundTrips()
    {
        var encryptor = new AesSettingEncryptor(NewKey());
        var cipher    = encryptor.Encrypt(string.Empty);
        Assert.Equal(string.Empty, encryptor.Decrypt(cipher));
    }

    [Fact]
    public void Encrypt_UnicodeContent_RoundTrips()
    {
        var encryptor = new AesSettingEncryptor(NewKey());
        var plaintext = "مفتاح سري 🔒 emoji test";
        var cipher    = encryptor.Encrypt(plaintext);
        Assert.Equal(plaintext, encryptor.Decrypt(cipher));
    }
}
