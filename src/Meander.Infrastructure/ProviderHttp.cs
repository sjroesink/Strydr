using Meander.Core;

namespace Meander.Infrastructure;

public sealed class ProviderBudget(OrsOptions options, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim concurrency = new(options.Concurrency);
    private readonly Dictionary<string, Queue<DateTimeOffset>> attempts = [];
    private readonly object gate = new();

    public async Task<IDisposable> EnterAsync(string path, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            var category = path.Contains("/directions/", StringComparison.Ordinal) ? "directions"
                : path.Contains("/snap/", StringComparison.Ordinal) ? "snap" : "geocode";
            var limit = category switch { "directions" => options.DirectionsPerMinute, "snap" => options.SnapsPerMinute, _ => options.GeocodingPerMinute };
            lock (gate)
            {
                if (!attempts.TryGetValue(category, out var queue)) attempts[category] = queue = [];
                var now = clock.GetUtcNow();
                while (queue.TryPeek(out var first) && first <= now.AddMinutes(-1)) queue.Dequeue();
                if (queue.Count >= limit)
                    throw new ProviderException(ProviderFailure.Throttled, "The routing service request budget is exhausted. Try again in a minute.");
                queue.Enqueue(now);
            }
            return new Lease(concurrency);
        }
        catch { concurrency.Release(); throw; }
    }
    public void Dispose() => concurrency.Dispose();
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) semaphore.Release(); }
    }
}

public sealed class ProviderBudgetHandler(ProviderBudget budget) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await budget.EnterAsync(request.RequestUri!.AbsolutePath, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
