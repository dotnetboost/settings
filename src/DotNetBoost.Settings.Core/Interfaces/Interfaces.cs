using DotNetBoost.Settings.Core.Models;
using System.Linq.Expressions;

namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>Entry-point for reading and writing strongly-typed settings groups.</summary>
public interface ISettingManager
{
    /// <summary>Returns a typed accessor for settings class <typeparamref name="T"/>.</summary>
    ISettingAccessor<T> For<T>() where T : new();
}

/// <summary>
/// Strongly-typed read/write accessor for settings group <typeparamref name="T"/>.
/// Obtain via <see cref="ISettingManager.For{T}"/>.
/// </summary>
public interface ISettingAccessor<T> where T : new()
{
    /// <summary>
    /// Blocking overload of <see cref="GetAsync(bool, CancellationToken)"/>. Prefer the async
    /// version; this one blocks the calling thread on the store read.
    /// </summary>
    T Get(bool refreshCache = false, CancellationToken cancellationToken = default);

    /// <summary>Blocking overload of <see cref="GetAsync{TProp}"/>.</summary>
    TProp Get<TProp>(Expression<Func<T, TProp>> selector,
                     bool refreshCache = false,
                     CancellationToken cancellationToken = default);

    /// <summary>
    /// Materialises the whole settings group. Properties with no stored row fall back to
    /// <c>[SettingDefault]</c>, then to the value the class itself initialises them to.
    /// </summary>
    /// <param name="refreshCache">Bypass the cache and reload from the store.</param>
    /// <param name="cancellationToken">Cancels the store read.</param>
    Task<T> GetAsync(bool refreshCache = false, CancellationToken cancellationToken = default);

    /// <summary>Reads a single property, using the same caching as a full read.</summary>
    /// <param name="selector">Must point directly at a property of <typeparamref name="T"/>.</param>
    /// <param name="refreshCache">Bypass the cache and reload from the store.</param>
    /// <param name="cancellationToken">Cancels the store read.</param>
    Task<TProp> GetAsync<TProp>(Expression<Func<T, TProp>> selector,
                                bool refreshCache = false,
                                CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and persists every property of <paramref name="model"/>, evicts the cache
    /// entry, records the change history, and fires registered change handlers.
    /// </summary>
    /// <exception cref="DotNetBoost.Settings.Core.SettingValidationException">
    /// A registered validator rejected the model; nothing is written.
    /// </exception>
    Task SetAsync(T model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one property. Reads the current group, applies the value, and writes the whole
    /// group back — so concurrent single-property writes can overwrite one another.
    /// </summary>
    /// <param name="selector">Must point directly at a property of <typeparamref name="T"/>.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    Task SetAsync<TProp>(Expression<Func<T, TProp>> selector,
                         TProp value,
                         CancellationToken cancellationToken = default);

    /// <summary>Whether the group has been persisted.</summary>
    /// <param name="allProperties">
    /// When <c>true</c>, requires a stored row for every property rather than at least one.
    /// </param>
    /// <param name="cancellationToken">Cancels the store read.</param>
    Task<bool> ExistsAsync(bool allProperties = false, CancellationToken cancellationToken = default);

    /// <summary>Deletes every stored row for the group and evicts its cache entry.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the group's current revision identifier, bypassing the cache. Hand this to a
    /// client (the REST endpoints send it as an <c>ETag</c>) so a later write can prove which
    /// revision it was based on.
    /// </summary>
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the group only if it is still at <paramref name="expectedVersion"/>.
    /// <para>
    /// This is what closes the read-edit-write window that a plain POCO cannot: the caller
    /// supplies the revision it based its edit on, rather than the write silently rebasing
    /// onto whatever is current.
    /// </para>
    /// </summary>
    /// <param name="model">The values to persist.</param>
    /// <param name="expectedVersion">
    /// A value previously returned by <see cref="GetVersionAsync"/>. <c>null</c> skips the
    /// check and behaves like <see cref="SetAsync(T, CancellationToken)"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    /// <exception cref="DotNetBoost.Settings.Core.SettingConcurrencyException">
    /// The group moved on since <paramref name="expectedVersion"/> was issued.
    /// </exception>
    Task SetAsync(T model, string? expectedVersion, CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates a settings model before it is persisted.
/// Called by both <c>SetAsync</c> (programmatic writes) and the REST API endpoint (POST).
/// </summary>
public interface ISettingValidator
{
    /// <summary>Validates a settings model, returning per-property error messages on failure.</summary>
    Task<(bool IsValid, IDictionary<string, string[]> Errors)> ValidateAsync(object model);

    /// <summary>Whether this validator handles <paramref name="type"/>. The first match wins.</summary>
    bool CanValidate(Type type);
}

/// <summary>
/// Encrypts and decrypts individual setting values for properties marked with
/// <see cref="DotNetBoost.Settings.Core.Attributes.SensitiveAttribute"/>.
/// </summary>
public interface ISettingEncryptor
{
    /// <summary>Encrypts a value for storage.</summary>
    string Encrypt(string plaintext);

    /// <summary>
    /// Decrypts a stored value. Throwing on failure is expected — the manager surfaces it as
    /// <see cref="DotNetBoost.Settings.Core.SettingDecryptionException"/> rather than silently
    /// falling back to the property's default.
    /// </summary>
    string Decrypt(string ciphertext);
}

/// <summary>
/// Persists a full history of setting changes.
/// Register an implementation to enable auditing.
/// </summary>
public interface ISettingAuditStore
{
    /// <summary>Appends one entry to the change history.</summary>
    Task RecordAsync(SettingAuditEntry entry, CancellationToken ct = default);

    /// <summary>Reads the change history for a group, optionally narrowed to a single property.</summary>
    Task<IReadOnlyList<SettingAuditEntry>> GetHistoryAsync(
        string group, string? key = null, CancellationToken ct = default);
}

/// <summary>
/// Receives a notification whenever the settings group <typeparamref name="T"/> is written.
/// Register one or more implementations in DI; all will be invoked in registration order.
/// </summary>
public interface ISettingChangedHandler<T> where T : new()
{
    /// <summary>
    /// Called after the group is written. Exceptions are logged and swallowed, so one failing
    /// handler cannot break the write or block the handlers after it.
    /// </summary>
    /// <param name="previous">The group as it was before the write, best-effort.</param>
    /// <param name="current">The group as written.</param>
    /// <param name="cancellationToken">Cancels the handler.</param>
    Task OnChangedAsync(T previous, T current, CancellationToken cancellationToken = default);
}

/// <summary>Optional marker interface for settings POCOs.</summary>
public interface ISettingGroup { }
