using DotNetBoost.Settings.Core;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// Table and schema names cannot be SQL parameters — the providers compose them into the
/// statement text — so validation is the only thing standing between a configuration value and
/// the query. These names come from application config rather than end users, but "trusted
/// input" is exactly the assumption that stops being true later.
/// </summary>
public class SettingTableOptionsTests
{
    [Fact]
    public void Defaults_MatchTheHistoricalNames()
    {
        var o = new SettingTableOptions();

        Assert.Null(o.Schema);
        Assert.Equal("Settings", o.SettingsTable);
        Assert.Equal("SettingAudits", o.AuditTable);
        Assert.Equal("Settings", o.QualifiedSettingsTable);
        Assert.Equal("SettingAudits", o.QualifiedAuditTable);
    }

    [Fact]
    public void Schema_QualifiesBothTables()
    {
        var o = new SettingTableOptions { Schema = "config" };

        Assert.Equal("config.Settings", o.QualifiedSettingsTable);
        Assert.Equal("config.SettingAudits", o.QualifiedAuditTable);
    }

    [Fact]
    public void IndexNames_FollowTheTableName()
    {
        // Two settings tables in one schema would otherwise collide on index name.
        var o = new SettingTableOptions { SettingsTable = "TenantSettings", AuditTable = "TenantAudits" };

        Assert.Equal("UX_TenantSettings_Group_Key", o.SettingsIndexName);
        Assert.Equal("IX_TenantAudits_Group_Key", o.AuditIndexName);
    }

    [Theory]
    [InlineData("Settings; DROP TABLE Users--")]
    [InlineData("Settings WHERE 1=1")]
    [InlineData("\"Settings\"")]
    [InlineData("[Settings]")]
    [InlineData("Set'tings")]
    [InlineData("dbo.Settings")]        // use Schema for this, not a dotted table name
    [InlineData("1Settings")]           // must not start with a digit
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_RejectsAnythingThatIsNotAPlainIdentifier(string name)
        => Assert.Throws<ArgumentException>(() => new SettingTableOptions { SettingsTable = name }.Validate());

    [Fact]
    public void Validate_RejectsAnInjectingSchema()
        => Assert.Throws<ArgumentException>(() => new SettingTableOptions { Schema = "a; DROP TABLE b--" }.Validate());

    [Fact]
    public void Validate_RejectsTheSameNameForBothTables()
    {
        var o = new SettingTableOptions { SettingsTable = "Config", AuditTable = "config" };
        var ex = Assert.Throws<ArgumentException>(o.Validate);
        Assert.Contains("must differ", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("app_settings")]
    [InlineData("_internal")]
    [InlineData("Settings2")]
    public void Validate_AcceptsPlainIdentifiers(string name)
        => new SettingTableOptions { SettingsTable = name }.Validate();

    [Fact]
    public void Validate_RejectsNamesOver128Characters()
        => Assert.Throws<ArgumentException>(
            () => new SettingTableOptions { SettingsTable = new string('a', 129) }.Validate());
}
