using DotNetBoost.Settings.Core.Attributes;

namespace SampleApp.Settings;

// Name pins the persistence key so this class can be renamed or moved later without
// orphaning its rows. It matches the class name here, so nothing moves today.
[SettingGroup("mail-server", Name = "MailSettings")]
public class MailSettings
{
    [SettingDefault("smtp.example.com")]
    public string Host { get; set; } = "smtp.example.com";

    [SettingDefault(587)]
    public int Port { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    // Encrypted at rest via AES-256-GCM — never stored as plaintext.
    [Sensitive]
    public string Password { get; set; } = string.Empty;
}

[SettingGroup("payment", Name = "PaymentSettings")]
public class PaymentSettings
{
    public string GatewayUrl { get; set; } = "https://gateway.example.com";

    [Sensitive]
    public string ApiKey { get; set; } = string.Empty;

    public decimal MaxAmount   { get; set; } = 10_000m;
    public bool    SandboxMode { get; set; } = true;
}
