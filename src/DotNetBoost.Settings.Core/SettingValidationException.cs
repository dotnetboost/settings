namespace DotNetBoost.Settings.Core;

/// <summary>
/// Thrown by <c>SetAsync</c> when a registered <c>ISettingValidator</c> rejects the model.
/// This ensures validation is enforced for both programmatic and REST API writes.
/// </summary>
public sealed class SettingValidationException : Exception
{
    /// <summary>The settings class that failed validation.</summary>
    public Type                              SettingsType { get; }
    /// <summary>Validation messages, keyed by property name.</summary>
    public IDictionary<string, string[]>     Errors       { get; }

    /// <summary>Creates the exception from a validator's result.</summary>
    public SettingValidationException(
        Type settingsType,
        IDictionary<string, string[]> errors,
        string message)
        : base(message)
    {
        SettingsType = settingsType;
        Errors       = errors;
    }
}
