using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core storage provider.</summary>
    public static class EntityFrameworkBuilderExtensions
    {
        /// <summary>
        /// Configures Entity Framework Core as the settings provider.
        /// <para>
        /// <typeparamref name="TContext"/> only has to be a <see cref="DbContext"/> that calls
        /// <c>ApplySettings</c> in <c>OnModelCreating</c>. It needs no interface
        /// and no <c>DbSet</c> properties: the store reads its entities through
        /// <see cref="DbContext.Set{TEntity}()"/>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Accepts <see cref="ISettingWriteParticipant"/>s: it registers
        /// <see cref="EfCoreSettingWriteScope"/> over the same context, for them to enlist in.
        /// <para>
        /// Registers nothing against <see cref="DbContext"/> itself. The store is constructed
        /// from <typeparamref name="TContext"/> explicitly, so the library never claims a
        /// framework type it does not own — which in an application with two contexts would
        /// silently hand a bare <c>DbContext</c> dependency whichever one Settings was
        /// pointed at.
        /// </para>
        /// </remarks>
        public static SettingBuilder UseEntityFrameworkCore<TContext>(this SettingBuilder builder)
            where TContext : DbContext
        {
            SettingBuilderGuard.EnsureProviderNotConfigured(
                builder, "EntityFrameworkCore", supportsWriteParticipants: true);

            // The scope and the store wrap the same scoped TContext instance, which is the
            // whole guarantee a participant relies on: what it enlists is saved by the store.
            builder.Services.AddScoped(
                sp => new EfCoreSettingWriteScope(sp.GetRequiredService<TContext>()));
            builder.Services.AddScoped<ISettingStore>(
                sp => new EfCoreSettingStore(
                    sp.GetRequiredService<TContext>(), sp.GetRequiredService<EfCoreSettingWriteScope>()));
            return builder;
        }
    }
}
