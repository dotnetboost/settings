using DotNetBoost.Settings.Core.Attributes;

namespace DotNetBoost.Settings.UnitTests.Settings;

[SettingGroup("mail-server")]
public class MailSettings
{
    public string Host { get; set; } = "smtp.example.com";
    public int    Port { get; set; } = 587;
}
