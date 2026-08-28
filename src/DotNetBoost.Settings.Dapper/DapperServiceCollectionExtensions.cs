using Dapper;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Dapper;
using Microsoft.Extensions.Hosting;
using System.Data;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the Dapper storage provider.</summary>
    public static class DapperBuilderExtensions
    {
        /// <summary>Configures Dapper as the settings provider.</summary>
        /// <param name="builder">The settings builder.</param>
        /// <param name="connectionFactory">Creates the connection for each scope.</param>
        /// <param name="migrateSchema">
        /// When true, a hosted service creates the <c>Settings</c> and <c>SettingAudits</c>
        /// tables at startup if they are absent. Leave it off if schema is managed elsewhere.
        /// </param>
        public static SettingBuilder UseDapper(
            this SettingBuilder builder,
            Func<IServiceProvider, IDbConnection> connectionFactory,
            bool migrateSchema = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);

            DotNetBoost.Settings.Dapper.DapperSettingStore.RegisterTypeHandlers();

            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "Dapper");
            builder.Services.AddScoped(connectionFactory);
            builder.Services.AddScoped<ISettingStore, DotNetBoost.Settings.Dapper.DapperSettingStore>();

            if (migrateSchema)
                builder.Services.AddHostedService<DotNetBoost.Settings.Dapper.DapperMigrationHostedService>();

            return builder;
        }
    }
}
