using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Meander.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Meander.Infrastructure;

public sealed class CandidateStore(TimeProvider clock, IRouteSearchStore? saved = null) : ICandidateStore, IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    public void Put(RouteCandidate candidate)
    {
        var options = new MemoryCacheEntryOptions().SetSize(2048 + candidate.Geometry.Coordinates.Sum(p => p.Length * 8 + 32));
        if (candidate.ExpiresAt is { } expiry) options.SetAbsoluteExpiration(expiry);
        cache.Set(candidate.Id, candidate, options);
    }
    public RouteCandidate? Get(Guid id)
    {
        var candidate = cache.Get<RouteCandidate>(id);
        if (candidate?.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow()) { cache.Remove(id); candidate = null; }
        return candidate ?? saved?.GetCandidate(id);
    }
    public void PutDraft(Guid id, RouteDraft draft, DateTimeOffset expiresAt) => cache.Set($"draft:{id}", new CachedDraft(draft, expiresAt),
        new MemoryCacheEntryOptions().SetSize(4096 + draft.Route.Geometry.Count * 128L)
            .SetAbsoluteExpiration(expiresAt));
    public RouteDraft? GetDraft(Guid id)
    {
        var item = cache.Get<CachedDraft>($"draft:{id}");
        if (item is not null && item.ExpiresAt <= clock.GetUtcNow()) { cache.Remove($"draft:{id}"); item = null; }
        return item?.Draft ?? saved?.GetDraft(id);
    }
    public void Dispose() => cache.Dispose();
    private sealed record CachedDraft(RouteDraft Draft, DateTimeOffset ExpiresAt);
}

public static class GpxWriter
{
    public static byte[] Write(RouteCandidate candidate)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        var track = new XElement(ns + "trk",
            new XElement(ns + "name", $"Meander {candidate.DistanceKm:F2} km"),
            new XElement(ns + "desc", string.Join(" ", candidate.Warnings.Select(w => w.Message))),
            new XElement(ns + "trkseg", candidate.Geometry.Coordinates.Select(p =>
                new XElement(ns + "trkpt", new XAttribute("lat", Number(p[1])), new XAttribute("lon", Number(p[0])),
                    p.Length > 2 ? new XElement(ns + "ele", Number(p[2])) : null))));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "Meander"),
                new XElement(ns + "metadata", new XElement(ns + "name", "Meander route"),
                    new XElement(ns + "desc", "Routing: openrouteservice; map data: OpenStreetMap contributors.")), track));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
            document.Save(writer);
        return stream.ToArray();
    }
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
