using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Services;
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

    /// <summary>Builder methods for the change-history trail.</summary>
    public static class AuditBuilderExtensions
    {
        /// <summary>Plugs in a custom audit store to record full change history.</summary>
        public static SettingBuilder UseAuditStore<TAuditStore>(this SettingBuilder builder)
            where TAuditStore : class, ISettingAuditStore
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.AddScoped<ISettingAuditStore, TAuditStore>();
            return builder;
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
