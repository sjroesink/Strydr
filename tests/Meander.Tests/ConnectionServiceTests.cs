using Meander.Core;

namespace Meander.Tests;

public sealed class ConnectionServiceTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private readonly TestClock clock = new();
    private readonly MemoryConnectionStore store = new();
    private readonly FakeConnector connector = new();
    private ConnectionService Service() => new(store, [connector], clock);
    private void Connect(TimeSpan expiresIn, string? refresh = "refresh-1") =>
        store.Upsert(new(Session, ServiceKind.Garmin, new("access-1", refresh, clock.Now + expiresIn, "user-1"), clock.Now));

    [Fact]
    public async Task ReturnsStoredTokensWhileTheyAreValid()
    {
        Connect(TimeSpan.FromHours(1));
        Assert.Equal("access-1", (await Service().TokensAsync(Session, ServiceKind.Garmin, default)).AccessToken);
        Assert.Equal(0, connector.Refreshes);
    }

    [Fact]
    public async Task RefreshesNearExpiryOnceAndKeepsTheUserId()
    {
        Connect(TimeSpan.FromMinutes(4));
        var service = Service();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.TokensAsync(Session, ServiceKind.Garmin, default)));
        Assert.All(results, t => Assert.Equal("access-2", t.AccessToken));
        Assert.Equal(1, connector.Refreshes);
        Assert.Equal("user-1", store.Get(Session, ServiceKind.Garmin)!.Tokens.ExternalUserId);
    }

    [Fact]
    public async Task RejectedRefreshRemovesTheConnection()
    {
        Connect(TimeSpan.FromMinutes(1));
        connector.RejectRefresh = true;
        var failure = await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default));
        Assert.Equal(ProviderFailure.Unauthorized, failure.Failure);
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
    }

    [Fact]
    public async Task MissingOrUnconfiguredConnectionsAreUnauthorized()
    {
        Assert.Equal(ProviderFailure.Unauthorized, (await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default))).Failure);
        Connect(TimeSpan.FromHours(1));
        connector.IsConfigured = false;
        Assert.Empty(Service().Configured);
        Assert.Equal(ProviderFailure.Unauthorized, (await Assert.ThrowsAsync<ProviderException>(() => Service().TokensAsync(Session, ServiceKind.Garmin, default))).Failure);
    }

    [Fact]
    public async Task UseAsyncDropsTheConnectionWhenTheServiceRejectsIt()
    {
        Connect(TimeSpan.FromHours(1));
        var destination = new FakeDestination { Reject = true };
        await Assert.ThrowsAsync<ProviderException>(() => Service().UseAsync(Session, ServiceKind.Garmin,
            tokens => destination.SendAsync(Fixtures.Candidate(), Activity.Running, null, tokens, default), default));
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
    }

    [Fact]
    public void RemoveReturnsTheDeletedConnection()
    {
        Connect(TimeSpan.FromHours(1));
        Assert.Equal("access-1", Service().Remove(Session, ServiceKind.Garmin)!.Tokens.AccessToken);
        Assert.Null(store.Get(Session, ServiceKind.Garmin));
        Assert.Null(Service().Remove(Session, ServiceKind.Garmin));
    }
}
