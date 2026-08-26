using DotNetBoost.Settings.Core.Attributes;

namespace DotNetBoost.Settings.UnitTests.DuplicatedSettings;

[SettingGroup("mail-server-2")]
public class MailSettings
{
    public string Host { get; set; } = "smtp2.example.com";
}
