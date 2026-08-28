using System.Text.RegularExpressions;

namespace DotNetBoost.Settings.Core;

/// <summary>
/// Names the relational objects the settings stores read and write, so they can live in a
/// schema or under names that suit an existing database rather than the defaults.
/// <para>
/// Table and schema names cannot be passed as SQL parameters — they are composed into the
/// statement text — so every name here is validated as a plain identifier before use. Anything
/// containing a quote, semicolon, space, or other punctuation is rejected rather than escaped.
/// </para>
/// </summary>
public sealed partial class SettingTableOptions
{
    /// <summary>Schema the tables live in. <c>null</c> uses the connection's default.</summary>
    public string? Schema { get; set; }

    /// <summary>Table holding the settings rows. Default: <c>Settings</c>.</summary>
    public string SettingsTable { get; set; } = "Settings";

    /// <summary>Table holding the change history. Default: <c>SettingAudits</c>.</summary>
    public string AuditTable { get; set; } = "SettingAudits";

    /// <summary><see cref="SettingsTable"/> prefixed with <see cref="Schema"/> when one is set.</summary>
    public string QualifiedSettingsTable => Qualify(SettingsTable);

    /// <summary><see cref="AuditTable"/> prefixed with <see cref="Schema"/> when one is set.</summary>
    public string QualifiedAuditTable => Qualify(AuditTable);

    /// <summary>
    /// Index name for the unique (Group, Key) constraint. Derived from the table name so two
    /// settings tables in one schema do not collide.
    /// </summary>
    public string SettingsIndexName => $"UX_{SettingsTable}_Group_Key";

    /// <summary>Index name for the audit lookup, derived the same way.</summary>
    public string AuditIndexName => $"IX_{AuditTable}_Group_Key";

    /// <summary>
    /// Validates every name. Called by the providers before any SQL is composed.
    /// </summary>
    /// <exception cref="ArgumentException">A name is not a plain SQL identifier.</exception>
    public void Validate()
    {
        if (Schema is not null) Ensure(Schema, nameof(Schema));
        Ensure(SettingsTable, nameof(SettingsTable));
        Ensure(AuditTable, nameof(AuditTable));

        if (string.Equals(SettingsTable, AuditTable, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"SettingsTable and AuditTable are both '{SettingsTable}'; they must differ.",
                nameof(AuditTable));
        }
    }

    private string Qualify(string table) => Schema is null ? table : $"{Schema}.{table}";

    private static void Ensure(string value, string name)
    {
        if (!Identifier().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid SQL identifier for {name}. Letters, digits and " +
                "underscores only, starting with a letter or underscore, at most 128 characters.",
                name);
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
