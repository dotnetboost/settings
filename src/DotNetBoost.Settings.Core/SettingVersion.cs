using DotNetBoost.Settings.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace DotNetBoost.Settings.Core;

/// <summary>
/// Derives an opaque revision identifier for a whole settings group from its stored rows.
/// Used as the entity tag on the generated REST endpoints so a client can prove which
/// revision it edited.
/// </summary>
public static class SettingVersion
{
    /// <summary>
    /// Computes the version of a group. Stable for identical stored state, and different as
    /// soon as any property is added, removed, or written.
    /// </summary>
    public static string Compute(IEnumerable<Setting> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var buffer = new MemoryStream();
        foreach (var row in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            buffer.Write(Encoding.UTF8.GetBytes(row.Key));
            buffer.WriteByte(0);

            // Rows written before concurrency tokens existed carry no RowVersion; fall back to
            // the value so the group version still moves when such a row is edited.
            buffer.Write(row.RowVersion is { Length: > 0 }
                ? row.RowVersion
                : Encoding.UTF8.GetBytes(row.Value));
            buffer.WriteByte(0);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16].ToLowerInvariant();
    }
}
