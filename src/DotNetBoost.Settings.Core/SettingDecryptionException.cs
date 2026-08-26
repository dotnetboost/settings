namespace DotNetBoost.Settings.Core;

/// <summary>
/// Thrown when a <c>[Sensitive]</c> value cannot be decrypted while materialising a settings
/// group — most often because the encryption key was rotated without keeping the previous key
/// configured.
/// <para>
/// This is deliberately fatal. Swallowing it would hand the application a model whose secret
/// properties silently hold their compile-time defaults, so a rotation mistake would surface
/// as an app running on default credentials rather than as an error.
/// </para>
/// </summary>
public sealed class SettingDecryptionException : Exception
{
    /// <summary>Persistence key of the settings group that failed to materialise.</summary>
    public string Group { get; }

    /// <summary>Property whose stored value could not be decrypted.</summary>
    public string Key { get; }

    /// <summary>Creates the exception for a group/key pair that could not be decrypted.</summary>
    public SettingDecryptionException(string group, string key, Exception innerException)
        : base($"Failed to decrypt setting '{group}.{key}'. If the encryption key was rotated, " +
               "keep the previous key configured via UseAesEncryption(primary, retired...). " +
               "To fall back to default values instead of failing, call IgnoreDecryptionFailures().",
               innerException)
    {
        Group = group;
        Key   = key;
    }
}
