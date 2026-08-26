using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DotNetBoost.Settings.ApiTests;

/// <summary>
/// The end-to-end scenario the concurrency work exists for: two administrators load the same
/// settings page, edit it, and save. Over HTTP there is no object continuity between the GET
/// and the POST, so the revision has to travel as an entity tag.
/// </summary>
public class ETagConcurrencyTests
{
    private const string Url = "/api/settings/api-test";

    [Fact]
    public async Task Get_ReturnsAnETag()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync(Url);

        Assert.NotNull(response.Headers.ETag);
        Assert.False(string.IsNullOrWhiteSpace(response.Headers.ETag!.Tag.Trim('"')));
    }

    [Fact]
    public async Task ETag_IsStableWhileNothingChanges()
    {
        await using var app = await TestApp.StartAsync();

        var first  = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;
        var second = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ETag_ChangesAfterAWrite()
    {
        await using var app = await TestApp.StartAsync();
        var before = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;

        await Post(app, new { Host = "changed.example.com" }, before);

        var after = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Post_WithCurrentETag_Succeeds()
    {
        await using var app = await TestApp.StartAsync();
        var etag = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;

        var response = await Post(app, new { Host = "fresh.example.com" }, etag);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithStaleETag_Returns412_AndDoesNotWrite()
    {
        await using var app = await TestApp.StartAsync();
        var staleTag = (await app.Client.GetAsync(Url)).Headers.ETag!.Tag;

        // Someone else saves first, moving the group on.
        await Post(app, new { Host = "theirs.example.com" }, staleTag);

        var response = await Post(app, new { Host = "mine.example.com" }, staleTag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);

        var current = await (await app.Client.GetAsync(Url)).Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("theirs.example.com", current!["host"].ToString());
    }

    [Fact]
    public async Task Post_WithoutIfMatch_WritesUnconditionally_ByDefault()
    {
        // Backwards compatible: existing clients that send no If-Match keep working.
        await using var app = await TestApp.StartAsync();

        var response = await Post(app, new { Host = "blind.example.com" }, etag: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutIfMatch_Returns428_WhenRequired()
    {
        await using var app = await TestApp.StartAsync(requireIfMatch: true);

        var response = await Post(app, new { Host = "blind.example.com" }, etag: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithWildcardIfMatch_IsTreatedAsNoExpectation()
    {
        // "*" asserts only that the resource exists, which it always does here.
        await using var app = await TestApp.StartAsync(requireIfMatch: true);

        var response = await Post(app, new { Host = "wildcard.example.com" }, etag: "*");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> Post(TestApp app, object body, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = JsonContent.Create(body)
        };

        if (etag == "*")
            request.Headers.TryAddWithoutValidation("If-Match", "*");
        else if (etag is not null)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));

        return await app.Client.SendAsync(request);
    }
}
