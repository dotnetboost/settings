using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.EntityFrameworkCore;

/// <summary>
/// Where an <see cref="ISettingWriteParticipant"/> enlists entities in a settings write on the
/// Entity Framework Core store. Take it as a dependency; <c>UseEntityFrameworkCore()</c>
/// registers it scoped.
/// </summary>
/// <remarks>
/// <para>
/// It wraps the very context instance the store saves, so an entity added here goes out in the
/// store's own <c>SaveChangesAsync</c> and commits with the rows, or not at all. That is why a
/// participant adds through this rather than through a context of its own: an entity tracked on
/// any other context would never be saved, and nothing would say so. An entity type that is not
/// mapped on the settings context throws here instead, which aborts the write.
/// </para>
/// <para>
/// Add, never save. If the write does not commit, everything added here is detached again, so
/// it cannot be saved later by the application's next <c>SaveChangesAsync</c> on the same
/// context. Only the entity passed in is detached, so enlist standalone entities rather than
/// graphs with new related entities.
/// </para>
/// </remarks>
/// <param name="context">The context the settings store saves.</param>
public sealed class EfCoreSettingWriteScope(DbContext context)
{
    private readonly List<object> _enlisted = [];

    /// <summary>The context the settings store saves.</summary>
    internal DbContext Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    internal bool HasEnlisted => _enlisted.Count > 0;

    /// <summary>
    /// Tracks <paramref name="entity"/> as added on the settings store's context, to be saved
    /// with the current write.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The entity's type is not mapped on the settings context.
    /// </exception>
    public void Add(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Context.Add(entity);
        _enlisted.Add(entity);
    }

    /// <summary>The write committed what was enlisted; forget it without detaching.</summary>
    internal void Committed() => _enlisted.Clear();

    /// <summary>The write will not commit; detach what was enlisted.</summary>
    internal void Discard()
    {
        foreach (var entity in _enlisted)
            Context.Entry(entity).State = EntityState.Detached;

        _enlisted.Clear();
    }
}
