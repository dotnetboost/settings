using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.ApiTests;

/// <summary>
/// The generated routes carry their own OpenAPI tag, as the sibling DotNetBoost packages do.
/// Without it every consumer wraps the call in <c>MapGroup("")</c> — an empty prefix whose
/// only purpose is to hang a tag on.
/// </summary>
public class EndpointMetadataTests
{
    [Fact]
    public async Task GeneratedEndpoints_AreTagged()
    {
        await using var app = await TestApp.StartAsync();

        var tagged = SettingsEndpoints(app)
            .Where(e => e.Metadata.GetMetadata<ITagsMetadata>()?.Tags.Contains("Settings") == true)
            .ToList();

        Assert.NotEmpty(tagged);
        Assert.Equal(SettingsEndpoints(app).Count, tagged.Count);
    }

    [Fact]
    public async Task EveryGeneratedEndpoint_IsTagged()
    {
        await using var app = await TestApp.StartAsync();

        var untagged = SettingsEndpoints(app)
            .Where(e => e.Metadata.GetMetadata<ITagsMetadata>()?.Tags.Contains("Settings") != true)
            .Select(e => e.DisplayName)
            .ToList();

        Assert.Empty(untagged);
    }

    /// <summary>
    /// Two endpoints per group, GET and POST. Pinned so that adding a third — or leaving the
    /// removed /audit route behind — is a test failure rather than something to notice later.
    /// </summary>
    [Fact]
    public async Task EachGroup_GetsExactlyGetAndPost()
    {
        await using var app = await TestApp.StartAsync();

        var methods = SettingsEndpoints(app)
            .Where(e => e.RoutePattern.RawText == "api/settings/api-test/")
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["GET", "POST"], methods);
        Assert.DoesNotContain(SettingsEndpoints(app),
            e => e.RoutePattern.RawText!.EndsWith("/audit", StringComparison.Ordinal));
    }

    private static List<RouteEndpoint> SettingsEndpoints(TestApp app)
        => app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("api/settings/", StringComparison.Ordinal))
            .ToList();
}
