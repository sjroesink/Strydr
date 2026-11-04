using Meander.Core;

// Saved routes carry their search (and so the activity); drawn previews only live in the cache.
public static class RouteLookup
{
    public static (RouteCandidate Route, Activity Activity)? Find(Guid id, IRouteSearchStore searches, ICandidateStore cache)
    {
        var route = searches.GetCandidate(id) ?? cache.Get(id);
        if (route is null) return null;
        var activity = route.SearchId is { } searchId ? searches.Get(searchId)?.Request.Activity
            : (cache.GetDraft(id) ?? searches.GetDraft(id))?.Activity;
        return (route, activity ?? Activity.Running);
    }
}
