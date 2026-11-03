using Meander.Core;
using Meander.Infrastructure;

namespace Meander.Tests;

public sealed class BudgetTests
{
    [Fact]
    public async Task BudgetCountsAttemptsPerServiceAndReplenishes()
    {
        var clock = new TestClock();
        using var budget = new ProviderBudget(new() { DirectionsPerMinute = 1 }, clock);
        (await budget.EnterAsync("/v2/directions/foot-walking/geojson", default)).Dispose();
        var error = await Assert.ThrowsAsync<ProviderException>(() => budget.EnterAsync("/v2/directions/foot-walking/geojson", default));
        Assert.Equal(ProviderFailure.Throttled, error.Failure);
        (await budget.EnterAsync("/v2/snap/foot-walking/json", default)).Dispose();
        clock.Now = clock.Now.AddSeconds(61);
        (await budget.EnterAsync("/v2/directions/foot-walking/geojson", default)).Dispose();
    }
}
