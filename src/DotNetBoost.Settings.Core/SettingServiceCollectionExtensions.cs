using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Entry point for registering DotNetBoost.Settings.</summary>
    public static class SettingServiceCollectionExtensions
    {
        /// <summary>Registers core settings services and returns a builder for further configuration.</summary>
        public static SettingBuilder AddSettings(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddMemoryCache();
            services.TryAddSingleton<ISettingCache, SettingCache>();
            services.TryAddScoped<ISettingManager, SettingManager>();

            // Always resolvable, so nothing has to null-check it. Reports no actor until an
            // application registers one — null means "not captured", never a claim about who
            // acted. ASP.NET applications want .UseHttpContextActor() from the API package.
            services.TryAddScoped<ISettingActorAccessor, NullSettingActorAccessor>();

            // Signals nothing until an application opts in, so a single-instance deployment
            // needs no configuration and behaves exactly as it did before signalling existed.
            services.TryAddSingleton<ISettingChangeSignal, NullSettingChangeSignal>();

            return new SettingBuilder(services);
        }
    }

    /// <summary>Builder methods for the caching layer.</summary>
    public static class CacheBuilderExtensions
    {
        /// <summary>Replaces the default IMemoryCache-backed cache (e.g. with Redis).</summary>
        public static SettingBuilder UseCustomCache<TCache>(this SettingBuilder builder)
            where TCache : class, ISettingCache
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Replace(ServiceDescriptor.Singleton<ISettingCache, TCache>());
            return builder;
        }

        /// <summary>Sets the cache duration (default: 10 minutes).</summary>
        public static SettingBuilder WithCacheDuration(this SettingBuilder builder, TimeSpan duration)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
            builder.CacheDuration = duration;
            return builder;
        }
    }

    /// <summary>Builder methods for encrypting <c>[Sensitive]</c> properties.</summary>
    public static class EncryptionBuilderExtensions
    {
        /// <summary>
        /// Enables AES-256-GCM encryption for properties marked with <c>[Sensitive]</c>.
        /// </summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="base64Key">The primary key. Everything written from now on uses it.</param>
        /// <param name="retiredBase64Keys">
        /// Previously used keys, kept for decryption only. Supply these when rotating, so values
        /// written under the old key stay readable until each group has been rewritten.
        /// </param>
        public static SettingBuilder UseAesEncryption(
            this SettingBuilder builder, string base64Key, params string[] retiredBase64Keys)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
            ArgumentNullException.ThrowIfNull(retiredBase64Keys);
            builder.Services.AddSingleton<ISettingEncryptor>(
                _ => new AesSettingEncryptor(base64Key, retiredBase64Keys));
            return builder;
        }

        /// <summary>
        /// Downgrades an undecryptable <c>[Sensitive]</c> value from a fatal
        /// <see cref="DotNetBoost.Settings.Core.SettingDecryptionException"/> to a logged error,
        /// leaving the property on its default value.
        /// <para>
        /// Consider carefully: after a mishandled key rotation this is the difference between an
        /// application that fails loudly and one that quietly runs on default secrets.
        /// </para>
        /// </summary>
        public static SettingBuilder IgnoreDecryptionFailures(this SettingBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Options.ThrowOnDecryptionFailure = false;
            return builder;
        }

        /// <summary>Plugs in a custom <see cref="ISettingEncryptor"/> implementation.</summary>
        public static SettingBuilder UseCustomEncryption<TEncryptor>(this SettingBuilder builder)
            where TEncryptor : class, ISettingEncryptor
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddSingleton<ISettingEncryptor, TEncryptor>();
            return builder;
        }
    }

    /// <summary>Builder methods for the write-notification hook.</summary>
    public static class WriteObserverBuilderExtensions
    {
        /// <summary>
        /// Registers an observer called once after every completed write to any settings
        /// group, with the per-property diff, the actor and a correlation id.
        /// <para>
        /// Several may be registered; all run, in registration order. An observer's
        /// exception is logged and swallowed — see <see cref="ISettingWriteObserver"/>.
        /// </para>
        /// </summary>
        public static SettingBuilder UseWriteObserver<TObserver>(this SettingBuilder builder)
            where TObserver : class, ISettingWriteObserver
        {
            ArgumentNullException.ThrowIfNull(builder);

            // Add, not TryAdd: observers compose. A second registration is a second observer,
            // not a replacement for the first.
            builder.Services.AddScoped<ISettingWriteObserver, TObserver>();
            return builder;
        }

        /// <summary>
        /// Registers <see cref="LoggingSettingWriteObserver"/>, which logs one line per write
        /// naming the changed properties — never their values. Off unless you ask for it.
        /// </summary>
        public static SettingBuilder UseLoggingWriteObserver(this SettingBuilder builder)
            => builder.UseWriteObserver<LoggingSettingWriteObserver>();

        /// <summary>
        /// Replaces the default actor accessor, which reports no actor, so
        /// <see cref="SettingWrite.Actor"/> carries who made the change.
        /// </summary>
        public static SettingBuilder UseActorAccessor<TAccessor>(this SettingBuilder builder)
            where TAccessor : class, ISettingActorAccessor
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Replace(ServiceDescriptor.Scoped<ISettingActorAccessor, TAccessor>());
            return builder;
        }
    }

    /// <summary>Builder methods for the read-path projection hook.</summary>
    public static class ProjectionBuilderExtensions
    {
        /// <summary>
        /// Registers a projector that shapes <typeparamref name="TSettings"/> for
        /// <c>GET /api/settings/{route}</c>. Registered scoped, so a projector may depend on
        /// anything else the request scope offers.
        /// <para>
        /// Read path only, and never applied to the <c>ETag</c> — see
        /// <see cref="ISettingProjector{T}"/>. Programmatic reads through
        /// <c>ISettingManager</c> are unaffected.
        /// </para>
        /// </summary>
        public static SettingBuilder UseProjector<TSettings, TProjector>(this SettingBuilder builder)
            where TSettings  : new()
            where TProjector : class, ISettingProjector<TSettings>
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddScoped<ISettingProjector<TSettings>, TProjector>();
            return builder;
        }
    }

    /// <summary>Builder methods for the schema-constraint hook.</summary>
    public static class SchemaContributorBuilderExtensions
    {
        /// <summary>
        /// Registers a contributor that publishes validation constraints through
        /// <c>GET /api/settings/{route}/schema</c>, so a generated form can show the bounds the
        /// server will apply instead of hardcoding its own.
        /// <para>
        /// Contributors <b>compose</b>: every registered type whose <c>CanDescribe</c> returns
        /// true is consulted and their constraints merged, so this adds to whatever
        /// <c>UseFluentValidation</c> already publishes rather than replacing it. Register one
        /// to describe something this library cannot know about — a rule carried by an
        /// attribute belonging to another package, say.
        /// </para>
        /// <para>
        /// <b>Order matters on a collision.</b> When two contributors set the same constraint
        /// name on the same property, the first <em>registered</em> wins and the clash is
        /// logged at <c>Warning</c>. Putting this call before or after
        /// <c>UseFluentValidation()</c> is therefore a choice about which one's value survives.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Registering the same <typeparamref name="TContributor"/> twice registers it once:
        /// a contributor only describes, so a duplicate would describe the same group twice
        /// and read as a collision with itself. That is deliberately unlike
        /// <see cref="WriteObserverBuilderExtensions.UseWriteObserver{TObserver}"/>, where two
        /// registrations of one observer are two observers — because an observer <em>does</em>
        /// something.
        /// </remarks>
        public static SettingBuilder UseSchemaContributor<TContributor>(this SettingBuilder builder)
            where TContributor : class, ISettingSchemaContributor
        {
            ArgumentNullException.ThrowIfNull(builder);

            // TryAddEnumerable with a type descriptor: several *different* contributor types
            // all register, which is the point, while a repeat of one type is deduped.
            // Transient to match the contributor DotNetBoost.Settings.FluentValidation
            // registers, so the two are indistinguishable to a reader.
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Transient<ISettingSchemaContributor, TContributor>());

            return builder;
        }

        /// <summary>
        /// Registers a contributor built by <paramref name="factory"/>, for one needing
        /// something DI cannot construct it with — which a schema contributor often does, since
        /// its whole purpose is describing rules this library does not own.
        /// <para>
        /// See <see cref="UseSchemaContributor{TContributor}(SettingBuilder)"/> for how
        /// contributors compose and which registration wins a collision.
        /// </para>
        /// </summary>
        public static SettingBuilder UseSchemaContributor<TContributor>(
            this SettingBuilder builder, Func<IServiceProvider, TContributor> factory)
            where TContributor : class, ISettingSchemaContributor
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);

            // Plain Add, not TryAddEnumerable: two factories are not comparable by
            // implementation type, so deduping would silently drop one of them.
            builder.Services.Add(ServiceDescriptor.Transient<ISettingSchemaContributor>(factory));

            return builder;
        }
    }

    /// <summary>Builder methods for propagating writes between application instances.</summary>
    public static class ChangeSignalBuilderExtensions
    {
        /// <summary>
        /// Uses <typeparamref name="TSignal"/> to tell every instance that a group changed,
        /// re-checked at most every <paramref name="checkInterval"/> (default: 5 seconds).
        /// Registered as a singleton. See <see cref="ISettingChangeSignal"/>.
        /// </summary>
        public static SettingBuilder UseChangeSignal<TSignal>(
            this SettingBuilder builder, TimeSpan? checkInterval = null)
            where TSignal : class, ISettingChangeSignal
        {
            ArgumentNullException.ThrowIfNull(builder);
            SetCheckInterval(builder, checkInterval);
            builder.Services.Replace(ServiceDescriptor.Singleton<ISettingChangeSignal, TSignal>());
            return builder.PublishChangeSignals();
        }

        /// <summary>
        /// Uses the signal <paramref name="factory"/> creates.
        /// See <see cref="UseChangeSignal{TSignal}"/>.
        /// </summary>
        public static SettingBuilder UseChangeSignal(
            this SettingBuilder builder,
            Func<IServiceProvider, ISettingChangeSignal> factory,
            TimeSpan? checkInterval = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);
            SetCheckInterval(builder, checkInterval);
            builder.Services.Replace(ServiceDescriptor.Singleton(factory));
            return builder.PublishChangeSignals();
        }

        /// <summary>
        /// Propagates writes between instances through the application's
        /// <c>IDistributedCache</c> — Redis, SQL Server, or whatever is already shared.
        /// <para>
        /// After this, an instance notices another's write within
        /// <paramref name="checkInterval"/> (default: 5 seconds) rather than waiting out its
        /// own <c>CacheDuration</c>.
        /// </para>
        /// </summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="keyPrefix">
        /// Prefixes the entry holding each group's token. Make it distinct per application if
        /// several share a cache.
        /// </param>
        /// <param name="checkInterval">How often, at most, a cache hit re-checks the token.</param>
        public static SettingBuilder SynchronizeThroughDistributedCache(
            this SettingBuilder builder,
            string keyPrefix = DistributedCacheSettingChangeSignal.DefaultKeyPrefix,
            TimeSpan? checkInterval = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

            return builder.UseChangeSignal(
                sp => new DistributedCacheSettingChangeSignal(
                    sp.GetService<IDistributedCache>() ?? throw new InvalidOperationException(
                        "SynchronizeThroughDistributedCache() needs an IDistributedCache that every " +
                        "instance shares. Register one, e.g. services.AddStackExchangeRedisCache(...)."),
                    keyPrefix),
                checkInterval);
        }

        /// <summary>
        /// Publishes a signal on every write, as an <see cref="ISettingWriteObserver"/> — one
        /// notification path rather than two, which is also how <c>ClearAsync</c> comes to
        /// signal without a second call site. Idempotent; the <c>UseChangeSignal</c> overloads
        /// call it for you.
        /// </summary>
        private static SettingBuilder PublishChangeSignals(this SettingBuilder builder)
        {
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Scoped<ISettingWriteObserver, ChangeSignalPublisher>());
            return builder;
        }

        private static void SetCheckInterval(SettingBuilder builder, TimeSpan? checkInterval)
        {
            if (checkInterval is not { } interval) return;

            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero, nameof(checkInterval));
            builder.Options.ChangeCheckInterval = interval;
        }
    }

    /// <summary>Builder methods for runtime change notifications.</summary>
    public static class ChangeNotificationBuilderExtensions
    {
        /// <summary>
        /// Registers a change handler that is called whenever <typeparamref name="TSettings"/>
        /// is written via <c>SetAsync</c>.
        /// </summary>
        public static SettingBuilder OnChanged<TSettings, THandler>(this SettingBuilder builder)
            where TSettings : new()
            where THandler  : class, ISettingChangedHandler<TSettings>
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddScoped<ISettingChangedHandler<TSettings>, THandler>();
            return builder;
        }
    }
}
