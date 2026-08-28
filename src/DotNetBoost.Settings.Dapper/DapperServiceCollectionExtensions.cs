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
        /// When true, a hosted service creates the settings and audit tables at startup if they
        /// are absent. Leave it off if schema is managed elsewhere.
        /// </param>
        /// <param name="configureTables">
        /// Overrides the schema and table names. Defaults to <c>Settings</c> and
        /// <c>SettingAudits</c> in the connection's default schema.
        /// </param>
        public static SettingBuilder UseDapper(
            this SettingBuilder builder,
            Func<IServiceProvider, IDbConnection> connectionFactory,
            bool migrateSchema = false,
            Action<SettingTableOptions>? configureTables = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);

            DotNetBoost.Settings.Dapper.DapperSettingStore.RegisterTypeHandlers();

            var tables = new SettingTableOptions();
            configureTables?.Invoke(tables);
            tables.Validate();   // fail at startup, not on the first query

            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "Dapper");
            builder.Services.AddSingleton(tables);
            builder.Services.AddScoped(connectionFactory);
            builder.Services.AddScoped<ISettingStore>(
                sp => new DotNetBoost.Settings.Dapper.DapperSettingStore(
                    sp.GetRequiredService<IDbConnection>(), tables));

            if (migrateSchema)
                builder.Services.AddHostedService<DotNetBoost.Settings.Dapper.DapperMigrationHostedService>();

            return builder;
        }
    }
}
