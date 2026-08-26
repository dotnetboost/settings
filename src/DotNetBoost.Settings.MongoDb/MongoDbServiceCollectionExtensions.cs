using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.MongoDb;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the MongoDB storage provider.</summary>
    public static class MongoBuilderExtensions
    {
        /// <summary>Configures MongoDB as the settings provider.</summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="connectionString">MongoDB connection string.</param>
        /// <param name="databaseName">Database holding the <c>settings</c> collection.</param>
        /// <param name="createIndexes">
        /// When true (the default) a hosted service creates the unique (Group, Key) index once
        /// at startup. Set it to false if the application's MongoDB user has no index-creation
        /// rights, or the index is managed out of band — the store's upsert semantics assume
        /// the index exists either way.
        /// </param>
        public static SettingBuilder UseMongoDb(
            this SettingBuilder builder,
            string connectionString,
            string databaseName,
            bool createIndexes = true)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "MongoDb");
            builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
            builder.Services.AddSingleton<IMongoDatabase>(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
            builder.Services.AddScoped<ISettingStore, DotNetBoost.Settings.MongoDb.MongoSettingStore>();

            if (createIndexes)
                builder.Services.AddHostedService<DotNetBoost.Settings.MongoDb.MongoIndexInitializer>();

            return builder;
        }
    }
}
