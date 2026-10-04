using DotNetBoost.Settings.API;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Primitives;
using System.Text.Json;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Generates REST endpoints for every <c>[SettingGroup]</c> class in the loaded assemblies.
/// </summary>
public static class SettingsMinimalApiExtensions
{
    private static readonly ConcurrentDictionary<Type, Func<ISettingManager, object>>
        AccessorFactories = new();

    private static readonly ConcurrentDictionary<Type, Func<object, bool, CancellationToken, Task<object?>>>
        GetterDelegates = new();

    private static readonly ConcurrentDictionary<Type, Func<object, object, string?, CancellationToken, Task>>
        SetterDelegates = new();

    private static readonly ConcurrentDictionary<Type, Func<object, CancellationToken, Task<string>>>
        VersionDelegates = new();

    private static readonly ConcurrentDictionary<Type, Type> ProjectorTypes = new();

    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task<object>>>
        ProjectorDelegates = new();

    /// <summary>
    /// OpenAPI tag the generated endpoints are grouped under, matching how the sibling
    /// DotNetBoost packages tag theirs. Without it every consumer has to wrap the call in
    /// <c>MapGroup("")</c> purely to carry a tag.
    /// </summary>
    private const string EndpointTag = "Settings";

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Registers GET and POST endpoints for every <c>[SettingGroup]</c>-decorated class.
    /// </summary>
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
        => endpoints.MapSettingsEndpoints(requireIfMatch: false);

    /// <summary>
    /// Registers GET and POST endpoints for every <c>[SettingGroup]</c>-decorated class.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="requireIfMatch">
    /// When true, a POST without an <c>If-Match</c> header is rejected with
    /// <c>428 Precondition Required</c> instead of writing unconditionally. Turn this on once
    /// every client round-trips the <c>ETag</c> returned by GET.
    /// </param>
    /// <param name="includeDiscovery">
    /// When true (the default), also registers <c>GET /api/settings</c> listing the registered
    /// groups and <c>GET /api/settings/{route}/schema</c> describing one group's properties.
    /// <para>
    /// Neither returns a stored value. The schema endpoint is covered by its group's
    /// <c>[Authorize]</c> like the group's other endpoints; the list is anonymous, and what it
    /// reveals is the set of route segments and persistence keys — which the per-group
    /// endpoints, anonymous by default and named after the class, already make guessable. Turn
    /// it off if you would rather not publish that inventory.
    /// </para>
    /// </param>
    public static void MapSettingsEndpoints(
        this IEndpointRouteBuilder endpoints, bool requireIfMatch, bool includeDiscovery = true)
    {
        var settingTypes = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(SafeTypes)
            .Where(t => t.GetCustomAttribute<SettingGroupAttribute>() is not null)
            .ToList();

        foreach (var type in settingTypes)
        {
            var attr  = type.GetCustomAttribute<SettingGroupAttribute>()!;
            var auth  = type.GetCustomAttribute<AuthorizeAttribute>();
            var group = endpoints.MapGroup($"api/settings/{attr.Route}").WithTags(EndpointTag);

            if (auth is not null)
                group.RequireAuthorization(auth);

            RegisterGet(group, type);
            RegisterPost(group, type, requireIfMatch);

            // Inside the same group, so it inherits the RequireAuthorization above: a group's
            // endpoints should not disagree about who may look at it.
            if (includeDiscovery) RegisterSchemaGet(group, type);
        }

        if (includeDiscovery) RegisterGroupList(endpoints, settingTypes);
    }

    /// <summary>
    /// <c>GET /api/settings</c> — the registered groups. Routes and persistence keys only;
    /// nothing here reads the store.
    /// </summary>
    private static void RegisterGroupList(IEndpointRouteBuilder endpoints, List<Type> settingTypes)
    {
        // Resolved once at startup rather than per request: the set of [SettingGroup] classes
        // cannot change while the application runs.
        var groups = settingTypes
            .Select(t => new SettingGroupDescriptor(
                Route:                 t.GetCustomAttribute<SettingGroupAttribute>()!.Route,
                Name:                  SettingGroupAttribute.ResolveName(t),
                RequiresAuthorization: t.GetCustomAttribute<AuthorizeAttribute>() is not null))
            .OrderBy(g => g.Route, StringComparer.Ordinal)
            .ToList();

        endpoints.MapGet("api/settings", () => Results.Ok(groups))
            .WithTags(EndpointTag)
            .WithName("ListSettingGroups")
            .WithSummary("Lists the registered settings groups. Returns no values.")
            .Produces<IReadOnlyList<SettingGroupDescriptor>>();
    }

    /// <summary>
    /// <c>GET /api/settings/{route}/schema</c> — what the group's properties are, so a client
    /// can generate a form from the class instead of guessing at the shape of a response.
    /// </summary>
    private static void RegisterSchemaGet(RouteGroupBuilder group, Type type)
    {
        var route     = type.GetCustomAttribute<SettingGroupAttribute>()!.Route;
        var groupName = SettingGroupAttribute.ResolveName(type);

        // The property list is a fact about the class, so it is built once. Constraints are
        // resolved per request, because a contributor is a registered service.
        var properties = SettingSchema.Describe(type);

        group.MapGet("/schema", (IServiceProvider sp) =>
        {
            var constraints = DescribeConstraints(sp, type);

            var described = properties
                .Select(p => new SettingPropertyDescriptor(
                    Name:      JsonNamingPolicy.CamelCase.ConvertName(p.Name),
                    Type:      p.ClrType,
                    Nullable:  p.IsNullable,

                    // Withheld for a [Sensitive] property. This endpoint is meant to be safe
                    // to expose more widely than values are, and a compile-time default on a
                    // secret property is a value like any other.
                    Default:   p.IsSensitive ? null : p.DefaultValue,
                    Sensitive: p.IsSensitive,
                    Constraints: constraints.GetValueOrDefault(p.Name)))
                .ToList();

            return Results.Ok(new SettingGroupSchema(route, groupName, described));
        })
        .WithName($"Schema{type.Name}")
        .WithSummary($"Describes the {type.Name} properties. Returns no values.")
        .Produces<SettingGroupSchema>();
    }

    /// <summary>
    /// Constraints for a group, from the first registered contributor that can describe it.
    /// First match wins, as it does for <c>ISettingValidator</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> DescribeConstraints(
        IServiceProvider sp, Type type)
    {
        var contributor = sp.GetServices<ISettingSchemaContributor>().FirstOrDefault(c => c.CanDescribe(type));
        if (contributor is null) return EmptyConstraints;

        // A contributor is third-party code reflecting over validation rules. A schema is a
        // convenience, so one that cannot describe itself should cost the constraints, not
        // the endpoint.
        try   { return contributor.Describe(type) ?? EmptyConstraints; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sp.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory) is { } logger)
                LogContributorFailed(logger, contributor.GetType().Name, type.Name, ex);

            return EmptyConstraints;
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> EmptyConstraints
        = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

    private const string LoggerCategory = "DotNetBoost.Settings.API.Schema";

    private static readonly Action<ILogger, string, string, Exception?> LogContributorFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning, new EventId(1200, nameof(LogContributorFailed)),
            "Schema contributor {Contributor} failed for settings group {Group}; reporting no constraints.");

    private static void RegisterGet(RouteGroupBuilder group, Type type)
    {
        group.MapGet("/", async (HttpContext ctx, ISettingManager manager, CancellationToken ct) =>
        {
            var accessor = GetAccessor(manager, type);

            // Read the revision before the values, so the tag can never describe a state newer
            // than the body it is attached to.
            var version = await VersionDelegates.GetOrAdd(type, CreateVersionDelegate)(accessor, ct)
                .ConfigureAwait(false);

            var getter = GetterDelegates.GetOrAdd(type, CreateGetterDelegate);
            var result = await getter(accessor, false, ct).ConfigureAwait(false);

            // The tag describes the STORED revision and is set from the version read above —
            // deliberately before projection, and never derived from it. A tag covering
            // projected output would move with the request's language, or with data owned by
            // some other component, and If-Match would stop protecting the stored values.
            ctx.Response.Headers.ETag = $"\"{version}\"";

            // Resolved from the request scope: a projector is registered scoped and is
            // expected to depend on per-request state. Nothing registered means the group is
            // serialised exactly as before.
            var projectorType = ProjectorTypes.GetOrAdd(type, MakeProjectorType);
            if (ctx.RequestServices.GetService(projectorType) is { } projector && result is not null)
            {
                result = await ProjectorDelegates.GetOrAdd(type, CreateProjectorDelegate)(projector, result, ct)
                    .ConfigureAwait(false);
            }

            return Results.Ok(result);
        })
        .WithName($"Get{type.Name}")
        .WithSummary($"Returns the current {type.Name} settings.")
        .Produces(200, type);
    }

    private static void RegisterPost(RouteGroupBuilder group, Type type, bool requireIfMatch)
    {
        group.MapPost("/", async (HttpContext ctx, ISettingManager manager, IServiceProvider sp) =>
        {
            var ifMatch = ParseIfMatch(ctx.Request.Headers.IfMatch);

            // Presence is what the requirement is about, not whether a concrete tag came back:
            // "If-Match: *" is a valid precondition (RFC 9110 — the resource must exist), and
            // ParseIfMatch deliberately maps it to "no expectation".
            if (requireIfMatch && !ctx.Request.Headers.ContainsKey(HeaderNames.IfMatch))
            {
                return Results.Problem(
                    "This endpoint requires an If-Match header carrying the ETag returned by GET.",
                    statusCode: StatusCodes.Status428PreconditionRequired);
            }

            object? body;
            try { body = await JsonSerializer.DeserializeAsync(ctx.Request.Body, type, JsonOpts, ctx.RequestAborted).ConfigureAwait(false); }
            catch (JsonException) { return Results.BadRequest("Invalid JSON payload."); }
            if (body is null) return Results.BadRequest("Request body is required.");

            var validationResult = await ValidateAsync(body, type, sp).ConfigureAwait(false);
            if (validationResult is not null) return validationResult;

            var accessor = GetAccessor(manager, type);
            var setter   = SetterDelegates.GetOrAdd(type, CreateSetterDelegate);

            try
            {
                await setter(accessor, body, ifMatch, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (DotNetBoost.Settings.Core.SettingValidationException ex)
            {
                return Results.ValidationProblem(ex.Errors);
            }
            catch (DotNetBoost.Settings.Core.SettingConcurrencyException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status412PreconditionFailed);
            }

            return Results.NoContent();
        })
        .WithName($"Update{type.Name}")
        .WithSummary($"Updates the {type.Name} settings.")
        .Accepts(type, "application/json")
        .Produces(204)
        .ProducesValidationProblem();
    }

    private static object GetAccessor(ISettingManager manager, Type type)
        => AccessorFactories.GetOrAdd(type, t =>
        {
            var param  = Expression.Parameter(typeof(ISettingManager), "m");
            var method = typeof(ISettingManager).GetMethod(nameof(ISettingManager.For))!.MakeGenericMethod(t);
            var body   = Expression.Convert(Expression.Call(param, method), typeof(object));
            return Expression.Lambda<Func<ISettingManager, object>>(body, param).Compile();
        })(manager);

    private static Func<object, bool, CancellationToken, Task<object?>> CreateGetterDelegate(Type type)
    {
        var accessorType = typeof(ISettingAccessor<>).MakeGenericType(type);
        var method       = accessorType.GetMethod("GetAsync", [typeof(bool), typeof(CancellationToken)])!;
        return async (accessor, refresh, ct) =>
        {
            var task = (Task)method.Invoke(accessor, [refresh, ct])!;
            await task.ConfigureAwait(false);
            return task.GetType().GetProperty("Result")!.GetValue(task);
        };
    }

    private static Func<object, object, string?, CancellationToken, Task> CreateSetterDelegate(Type type)
    {
        var accessorType = typeof(ISettingAccessor<>).MakeGenericType(type);
        var method       = accessorType.GetMethod("SetAsync", [type, typeof(string), typeof(CancellationToken)])!;
        return async (accessor, body, expectedVersion, ct) =>
        {
            var task = (Task)method.Invoke(accessor, [body, expectedVersion, ct])!;
            await task.ConfigureAwait(false);
        };
    }

    private static Type MakeProjectorType(Type type)
        => typeof(ISettingProjector<>).MakeGenericType(type);

    private static Func<object, object, CancellationToken, Task<object>> CreateProjectorDelegate(Type type)
    {
        var method = MakeProjectorType(type).GetMethod(
            nameof(ISettingProjector<object>.ProjectAsync), [type, typeof(CancellationToken)])!;

        return async (projector, group, ct) =>
        {
            var task = (Task<object>)method.Invoke(projector, [group, ct])!;
            return await task.ConfigureAwait(false);
        };
    }

    private static Func<object, CancellationToken, Task<string>> CreateVersionDelegate(Type type)
    {
        var accessorType = typeof(ISettingAccessor<>).MakeGenericType(type);
        var method       = accessorType.GetMethod("GetVersionAsync", [typeof(CancellationToken)])!;
        return async (accessor, ct) =>
        {
            var task = (Task<string>)method.Invoke(accessor, [ct])!;
            return await task.ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Extracts a single entity tag from an If-Match header. Absent or <c>*</c> both mean
    /// "no expectation" — <c>*</c> asserts only that the resource exists, which it always does.
    /// </summary>
    private static string? ParseIfMatch(StringValues header)
    {
        var raw = header.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == "*") return null;

        // Take the first tag and strip the quotes and any weak-validator prefix.
        var first = raw.Split(',')[0].Trim();
        if (first.StartsWith("W/", StringComparison.Ordinal)) first = first[2..];
        return first.Trim('"');
    }

    private static async Task<IResult?> ValidateAsync(object body, Type type, IServiceProvider sp)
    {
        var validator = sp.GetServices<ISettingValidator>().FirstOrDefault(v => v.CanValidate(type));
        if (validator is not null)
        {
            var (isValid, errors) = await validator.ValidateAsync(body).ConfigureAwait(false);
            return isValid ? null : Results.ValidationProblem(errors);
        }

        var ctx     = new ValidationContext(body);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(body, ctx, results, true)) return null;

        var errs = results.ToDictionary(
            r => r.MemberNames.FirstOrDefault() ?? "Error",
            r => new[] { r.ErrorMessage ?? "Invalid value" });

        return Results.ValidationProblem(errs);
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
