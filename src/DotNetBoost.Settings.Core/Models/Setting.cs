namespace DotNetBoost.Settings.Core.Models;

/// <summary>Represents a single persisted setting entry.</summary>
public sealed class Setting
{
    /// <summary>Surrogate primary key. Generated on construction; stores keep it stable across updates.</summary>
    public Guid   Id        { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Persistence key of the settings group this row belongs to — the <c>Name</c> on
    /// <c>[SettingGroup]</c>, or the class name when none is set.
    /// </summary>
    public required string Group     { get; set; }

    /// <summary>Name of the property within the group.</summary>
    public required string Key       { get; set; }

    /// <summary>
    /// The serialised value. Simple types use their invariant string form; everything else is
    /// JSON. Holds ciphertext when <see cref="IsEncrypted"/> is set.
    /// </summary>
    public required string Value     { get; set; }

    /// <summary>Assembly-qualified-ish name of the CLR type <see cref="Value"/> was written from.</summary>
    public required string Type      { get; set; }

    /// <summary>Whether <see cref="Value"/> is ciphertext produced by the registered encryptor.</summary>
    public bool   IsEncrypted { get; set; }

    /// <summary>UTC timestamp of the last write.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who last wrote the value, when the application supplies it.</summary>
    public string? UpdatedBy  { get; set; }

    /// <summary>
    /// Optimistic concurrency token identifying the stored revision of this row.
    /// <para>
    /// On a row read from a store this is the row's current token. On a row handed to
    /// <c>UpsertAsync</c>/<c>UpsertManyAsync</c> it is the token the caller <em>expects</em>
    /// to still be stored: the write proceeds only if it matches, and the store then stamps a
    /// fresh token. <c>null</c> means "no expectation" — a blind write, which is what a first
    /// insert and a pre-versioning row both need.
    /// </para>
    /// </summary>
    public byte[]? RowVersion { get; set; }

    /// <summary>Generates a fresh concurrency token. Stores call this when writing a row.</summary>
    public static byte[] NewRowVersion() => Guid.NewGuid().ToByteArray();
}

/// <summary>Immutable record of a historical change to a setting value.</summary>
public sealed class SettingAuditEntry
{
    /// <summary>Surrogate primary key for the audit row.</summary>
    public Guid     Id          { get; set; } = Guid.NewGuid();

    /// <summary>Persistence key of the settings group the change belongs to.</summary>
    public required string Group       { get; set; }

    /// <summary>Name of the property that changed.</summary>
    public required string Key         { get; set; }

    /// <summary>Value before the change, or empty on first write. Encrypted values read <c>[encrypted]</c>.</summary>
    public required string OldValue    { get; set; }

    /// <summary>Value after the change. Encrypted values read <c>[encrypted]</c>.</summary>
    public required string NewValue    { get; set; }

    /// <summary>Who made the change.</summary>
    public required string ChangedBy   { get; set; }

    /// <summary>UTC timestamp of the change.</summary>
    public DateTime ChangedAt  { get; set; } = DateTime.UtcNow;
}
