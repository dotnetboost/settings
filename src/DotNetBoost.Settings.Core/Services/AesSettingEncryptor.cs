using DotNetBoost.Settings.Core.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace DotNetBoost.Settings.Core.Services;

/// <summary>
/// AES-256-GCM encryptor for properties marked with
/// <see cref="DotNetBoost.Settings.Core.Attributes.SensitiveAttribute"/>.
/// <para>
/// Values are written as <c>v1:{keyId}:{base64}</c>, where the Base64 payload is
/// nonce (12 bytes) || ciphertext || tag (16 bytes). Stamping the key id lets a value written
/// under a retired key be recognised and decrypted after a rotation, instead of failing with
/// nothing but an authentication error to go on.
/// </para>
/// <para>
/// To rotate: pass the new key first and keep the previous one in <c>retiredBase64Keys</c>.
/// Reads then work against both, and each <c>SetAsync</c> re-writes that group under the new
/// key. Once every group has been rewritten, the retired key can be dropped.
/// </para>
/// </summary>
public sealed class AesSettingEncryptor : ISettingEncryptor
{
    private const string EnvelopePrefix = "v1";
    private const char   Separator      = ':';

    /// <summary>Domain separator, so the stored key id is not a bare hash of the key.</summary>
    private static ReadOnlySpan<byte> KeyIdDomain => "dnb-setting-key-id"u8;

    private readonly AesKey                _primary;
    private readonly IReadOnlyList<AesKey> _keys;

    /// <summary>Short fingerprint of the key used for new writes. Safe to log.</summary>
    public string PrimaryKeyId => _primary.Id;

    /// <param name="base64Key">
    /// The primary 32-byte AES-256 key, Base64-encoded. All new values are encrypted with it.
    /// Generate with: <c>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>
    /// </param>
    /// <param name="retiredBase64Keys">
    /// Previously used keys, kept for decryption only. Required to read values written before
    /// a rotation; drop them once every group has been rewritten under the new key.
    /// </param>
    public AesSettingEncryptor(string base64Key, params string[] retiredBase64Keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        ArgumentNullException.ThrowIfNull(retiredBase64Keys);

        _primary = new AesKey(base64Key, nameof(base64Key));

        var keys = new List<AesKey>(retiredBase64Keys.Length + 1) { _primary };
        foreach (var retired in retiredBase64Keys)
            keys.Add(new AesKey(retired, nameof(retiredBase64Keys)));

        // A duplicated key would make the retired list a silent no-op; say so rather than
        // letting a rotation look configured when it is not.
        var duplicate = keys.GroupBy(k => k.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"The same AES key was supplied more than once (key id '{duplicate.Key}').",
                nameof(retiredBase64Keys));
        }

        _keys = keys;
    }

    /// <inheritdoc/>
    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce      = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintextB = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextB.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(_primary.Bytes, tag.Length);
        aes.Encrypt(nonce, plaintextB, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, nonce.Length);
        tag.CopyTo(result, nonce.Length + ciphertext.Length);

        return string.Concat(
            EnvelopePrefix, Separator, _primary.Id, Separator, Convert.ToBase64String(result));
    }

    /// <inheritdoc/>
    public string Decrypt(string ciphertext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);

        if (TryParseEnvelope(ciphertext, out var keyId, out var payload))
        {
            var key = _keys.FirstOrDefault(k => string.Equals(k.Id, keyId, StringComparison.Ordinal))
                ?? throw new CryptographicException(
                    $"This value was encrypted with AES key '{keyId}', which is not configured. " +
                    "If the key was rotated, pass the previous key to UseAesEncryption(primary, retired...).");

            return DecryptWith(Decode(payload), key);
        }

        // Written before key ids existed. AES-GCM authenticates, so a wrong key fails the tag
        // check rather than returning garbage — trying each key in turn is safe.
        var raw = Decode(ciphertext);

        foreach (var key in _keys)
        {
            try { return DecryptWith(raw, key); }
            catch (CryptographicException) { /* next key */ }
        }

        throw new CryptographicException(
            "None of the configured AES keys could decrypt this value. It predates key ids, so " +
            "the key that wrote it cannot be identified — keep the previous key configured via " +
            "UseAesEncryption(primary, retired...).");
    }

    private static bool TryParseEnvelope(string value, out string keyId, out string payload)
    {
        keyId = payload = string.Empty;

        // Base64 never contains ':', so the prefix is unambiguous against legacy values.
        if (!value.StartsWith(EnvelopePrefix + Separator, StringComparison.Ordinal)) return false;

        var rest      = value[(EnvelopePrefix.Length + 1)..];
        var separator = rest.IndexOf(Separator);
        if (separator <= 0) return false;

        keyId   = rest[..separator];
        payload = rest[(separator + 1)..];
        return true;
    }

    private static byte[] Decode(string base64)
    {
        try { return Convert.FromBase64String(base64); }
        catch (FormatException ex)
        {
            throw new CryptographicException("Encrypted value is not valid Base64.", ex);
        }
    }

    private static string DecryptWith(byte[] raw, AesKey key)
    {
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen   = AesGcm.TagByteSizes.MaxSize;

        if (raw.Length < nonceLen + tagLen)
            throw new CryptographicException("Ciphertext is too short to be a valid encrypted value.");

        var nonce     = raw[..nonceLen];
        var tag       = raw[^tagLen..];
        var encrypted = raw[nonceLen..^tagLen];
        var plaintext = new byte[encrypted.Length];

        using var aes = new AesGcm(key.Bytes, tag.Length);
        aes.Decrypt(nonce, encrypted, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private sealed class AesKey
    {
        public byte[] Bytes { get; }
        public string Id    { get; }

        public AesKey(string base64Key, string paramName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(base64Key, paramName);

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64Key); }
            catch (FormatException ex)
            {
                throw new ArgumentException("AES key must be valid Base64.", paramName, ex);
            }

            if (bytes.Length != 32)
                throw new ArgumentException("AES-256 key must be exactly 32 bytes.", paramName);

            Bytes = bytes;

            var buffer = new byte[KeyIdDomain.Length + bytes.Length];
            KeyIdDomain.CopyTo(buffer);
            bytes.CopyTo(buffer, KeyIdDomain.Length);
            Id = Convert.ToHexString(SHA256.HashData(buffer))[..8].ToLowerInvariant();
        }
    }
}
