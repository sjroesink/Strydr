using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Meander.Tests;

public sealed class PwaHostingTests
{
    [Fact]
    public async Task ShellAndWorkerRevalidateAndOnlyExplicitSpaRoutesFallBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "meander-pwa-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>Meander shell</html>");
        await File.WriteAllTextAsync(Path.Combine(root, "sw.js"), "// worker");
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.webmanifest"), "{\"name\":\"Meander\"}");
        try
        {
            using var basis = new ApiFactory(new StubOrs());
            using var factory = basis.WithWebHostBuilder(builder => builder.UseWebRoot(root));
            using var client = factory.CreateClient();
            foreach (var path in new[] { "/", "/index.html", "/routes/a-saved-search", "/sw.js", "/manifest.webmanifest" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoCache, path);
            }
            using var manifest = await client.GetAsync("/manifest.webmanifest");
            Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
            using var worker = await client.GetAsync("/sw.js");
            Assert.Contains("javascript", worker.Content.Headers.ContentType!.MediaType!);
            Assert.Equal("/", worker.Headers.GetValues("Service-Worker-Allowed").Single());
            foreach (var path in new[] { "/unknown-page", "/api/missing", "/map/missing", "/missing.js" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.DoesNotContain("Meander shell", await response.Content.ReadAsStringAsync());
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
