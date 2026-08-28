using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.MongoDb;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the MongoDB storage provider.</summary>
    public static class MongoBuilderExtensions
    {
        /// <summary>
        /// Configures MongoDB as the settings provider, using a client owned by this provider.
        /// <para>
        /// Neither <see cref="IMongoClient"/> nor <see cref="IMongoDatabase"/> is registered in
        /// the container, so this cannot collide with an application that configures MongoDB
        /// itself. If your application already has a client — through .NET Aspire, or its own
        /// registration — prefer the
        /// <see cref="UseMongoDb(SettingBuilder, Func{IServiceProvider, IMongoDatabase}, bool, string)"/>
        /// overload so the settings store shares it.
        /// </para>
        /// </summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="connectionString">MongoDB connection string.</param>
        /// <param name="databaseName">Database holding the <c>settings</c> collection.</param>
        /// <param name="createIndexes">
        /// When true (the default) a hosted service creates the unique (Group, Key) index once
        /// at startup. Set it to false if the application's MongoDB user has no index-creation
        /// rights, or the index is managed out of band — the store's upsert semantics assume
        /// the index exists either way.
        /// </param>
        /// <param name="collectionName">Collection to store settings in. Defaults to <c>settings</c>.</param>
        public static SettingBuilder UseMongoDb(
            this SettingBuilder builder,
            string connectionString,
            string databaseName,
            bool createIndexes = true,
            string collectionName = MongoSettingStore.DefaultCollectionName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            // Built inside the factory below, which runs once, so the client is still created
            // lazily and shared — it just never enters the container under a shared type.
            return builder.UseMongoDb(
                _ => new MongoClient(connectionString).GetDatabase(databaseName),
                createIndexes, collectionName);
        }

        /// <summary>
        /// Configures MongoDB as the settings provider against a database the application
        /// supplies — letting the settings store reuse a client that is already configured with
        /// its own credentials, telemetry and resilience settings.
        /// </summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="databaseFactory">
        /// Resolves the database to store settings in. Invoked once; typically
        /// <c>sp =&gt; sp.GetRequiredService&lt;IMongoClient&gt;().GetDatabase("my_app")</c>.
        /// </param>
        /// <param name="createIndexes">
        /// When true (the default) a hosted service creates the unique (Group, Key) index once
        /// at startup.
        /// </param>
        /// <param name="collectionName">Collection to store settings in. Defaults to <c>settings</c>.</param>
        public static SettingBuilder UseMongoDb(
            this SettingBuilder builder,
            Func<IServiceProvider, IMongoDatabase> databaseFactory,
            bool createIndexes = true,
            string collectionName = MongoSettingStore.DefaultCollectionName)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(databaseFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "MongoDb");

            builder.Services.AddSingleton(sp => new SettingsMongoContext(databaseFactory(sp), collectionName));
            builder.Services.AddScoped<ISettingStore>(sp =>
            {
                var context = sp.GetRequiredService<SettingsMongoContext>();
                return new MongoSettingStore(context.Database, context.CollectionName);
            });

            if (createIndexes)
                builder.Services.AddHostedService<MongoIndexInitializer>();

            return builder;
        }
    }
}
