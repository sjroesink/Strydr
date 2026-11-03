using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meander.Core;

namespace Meander.Tests;

public sealed class RouteEditorTests
{
    [Fact]
    public async Task EditorPreservesExactRouteAndSaveCreatesAnIndependentPermanentSearchWithoutProviderCalls()
    {
        var upstream = new StubOrs();
        await using var app = new ApiFactory(upstream);
        using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/routes/generate", new { start = Fixtures.Start, targetDistanceKm = 5 });
        var original = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        var calls = upstream.Requests.Count;
        var editResponse = await client.PostAsync($"/api/routes/{original.GetProperty("id").GetString()}/edit", null);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        var edit = await editResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original.GetProperty("geometry").GetRawText(), edit.GetProperty("preview").GetProperty("geometry").GetRawText());
        Assert.True(edit.GetProperty("isLoop").GetBoolean());
        Assert.InRange(edit.GetProperty("points").GetArrayLength(), 1, 19);
        var savedResponse = await client.PostAsJsonAsync("/api/routes/save", edit.GetProperty("request"));
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = (await savedResponse.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.NotEqual(original.GetProperty("searchId").GetGuid(), saved.GetProperty("searchId").GetGuid());
        Assert.Equal(original.GetProperty("geometry").GetRawText(), saved.GetProperty("geometry").GetRawText());
        Assert.Equal(calls, upstream.Requests.Count);
        var reopened = await client.GetFromJsonAsync<JsonElement>($"/api/searches/{saved.GetProperty("searchId").GetString()}");
        Assert.True(reopened.GetProperty("request").GetProperty("isEditedRoute").GetBoolean());
        Assert.Equal(edit.GetProperty("points").GetArrayLength() + 1, reopened.GetProperty("drawnPoints").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/routes/{saved.GetProperty("id").GetString()}/gpx")).StatusCode);
        var old = await client.GetFromJsonAsync<JsonElement>($"/api/searches/{original.GetProperty("searchId").GetString()}");
        Assert.Equal(original.GetProperty("geometry").GetRawText(), old.GetProperty("candidates")[0].GetProperty("geometry").GetRawText());
    }

    [Fact]
    public async Task ChangedPointsAreRoutedAndSavingUsesThatExactPreview()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var start = Fixtures.Start;
        var end = Geo.Destination(start, 90, 1);
        var moved = Geo.Destination(start, 0, .5);
        var previewResponse = await client.PostAsJsonAsync("/api/routes/preview", new { start, points = new[] { moved, end } });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var savedResponse = await client.PostAsJsonAsync("/api/routes/save", new {
            start, end, targetDistanceKm = 5, drawnRouteId = preview.GetProperty("id").GetString()
        });
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = (await savedResponse.Content.ReadFromJsonAsync<JsonElement>())[0];
        Assert.Equal(preview.GetProperty("geometry").GetRawText(), saved.GetProperty("geometry").GetRawText());
        Assert.Equal(preview.GetProperty("distanceKm").GetDouble(), saved.GetProperty("distanceKm").GetDouble());
    }

    [Fact]
    public async Task MissingOrMismatchedEditUsesProblemDetails()
    {
        await using var app = new ApiFactory(new());
        using var client = app.CreateClient();
        var missing = await client.PostAsync($"/api/routes/{Guid.NewGuid()}/edit", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var previewResponse = await client.PostAsJsonAsync("/api/routes/preview", new {
            start = Fixtures.Start, points = new[] { Geo.Destination(Fixtures.Start, 90, .5), Fixtures.Start }
        });
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        var invalid = await client.PostAsJsonAsync("/api/routes/save", new {
            start = Fixtures.Start, targetDistanceKm = 5, activity = "walking", drawnRouteId = preview.GetProperty("id").GetString()
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType!.MediaType);
    }
}
