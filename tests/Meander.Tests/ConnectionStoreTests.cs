using Meander.Core;
using Meander.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Meander.Tests;

public sealed class ConnectionStoreTests
{
    private static ServiceConnection Connection(Guid session, string user = "garmin-user") =>
        new(session, ServiceKind.Garmin, new("secret-access", "secret-refresh", DateTimeOffset.UtcNow.AddHours(1), user), DateTimeOffset.UtcNow);

    [Fact]
    public void RoundTripsUpsertsAndDeletes()
    {
        using var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = ":memory:" }, new EphemeralDataProtectionProvider());
        var session = Guid.NewGuid();
        store.Upsert(Connection(session));
        Assert.Equal("secret-access", store.Get(session, ServiceKind.Garmin)!.Tokens.AccessToken);
        store.Upsert(Connection(session) with { Tokens = Connection(session).Tokens with { AccessToken = "newer" } });
        Assert.Equal("newer", Assert.Single(store.List(session)).Tokens.AccessToken);
        store.Delete(session, ServiceKind.Garmin);
        Assert.Null(store.Get(session, ServiceKind.Garmin));
    }

    [Fact]
    public void DeletesEveryConnectionOfAnExternalUser()
    {
        using var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = ":memory:" }, new EphemeralDataProtectionProvider());
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        store.Upsert(Connection(a)); store.Upsert(Connection(b)); store.Upsert(Connection(c, "someone-else"));
        Assert.Equal(2, store.DeleteByExternalUser(ServiceKind.Garmin, "garmin-user"));
        Assert.Null(store.Get(a, ServiceKind.Garmin));
        Assert.NotNull(store.Get(c, ServiceKind.Garmin));
    }

    [Fact]
    public void StoresNoPlainTokensOrUserIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider()))
                store.Upsert(Connection(Guid.NewGuid()));
            using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "SELECT external_user || json FROM connections";
            var stored = (string)command.ExecuteScalar()!;
            Assert.DoesNotContain("secret-", stored);
            Assert.DoesNotContain("garmin-user", stored);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void UnreadableRowsCountAsDisconnected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meander-{Guid.NewGuid():N}.sqlite");
        try
        {
            var session = Guid.NewGuid();
            using (var store = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider()))
                store.Upsert(Connection(session));
            // Same database, different key ring: as after losing data/keys.
            using var other = new SqliteConnectionStore(new ResultStorageOptions { DatabasePath = path }, new EphemeralDataProtectionProvider());
            Assert.Null(other.Get(session, ServiceKind.Garmin));
            Assert.Empty(other.List(session));
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public void PkceMatchesTheRfcExampleAndIsRandom()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", OAuthPkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (first, second) = (OAuthPkce.Create(), OAuthPkce.Create());
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.State, second.State);
        Assert.Equal(OAuthPkce.ChallengeFor(first.Verifier), first.Challenge);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Verifier);
    }
}
