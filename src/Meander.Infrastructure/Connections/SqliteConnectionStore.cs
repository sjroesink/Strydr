using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meander.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Meander.Infrastructure;

// Tokens are encrypted with Data Protection; the external user id is only kept as a hash so
// deregistration webhooks can find it. Unlike saved searches, connections change and disappear.
public sealed class SqliteConnectionStore : IConnectionStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly SqliteConnection connection;
    private readonly IDataProtector protector;
    private readonly object gate = new();

    public SqliteConnectionStore(ResultStorageOptions options, IDataProtectionProvider protection)
    {
        protector = protection.CreateProtector("Meander.Connections.v1");
        if (string.IsNullOrWhiteSpace(options.DatabasePath)) throw new InvalidOperationException("ResultStorage:DatabasePath is required.");
        if (options.DatabasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS connections (
                session_id TEXT NOT NULL, service TEXT NOT NULL, external_user TEXT, json TEXT NOT NULL,
                PRIMARY KEY (session_id, service));
            CREATE INDEX IF NOT EXISTS connections_external_user ON connections (service, external_user);
            CREATE TABLE IF NOT EXISTS sent_routes (
                session_id TEXT NOT NULL, service TEXT NOT NULL, route_id TEXT NOT NULL,
                external_id TEXT NOT NULL, view_url TEXT, sent_at TEXT NOT NULL,
                PRIMARY KEY (session_id, service, route_id));
            """;
        command.ExecuteNonQuery();
    }

    public ServiceConnection? Get(Guid sessionId, ServiceKind service) =>
        Query("SELECT json FROM connections WHERE session_id = $session AND service = $service",
            ("$session", sessionId.ToString()), ("$service", service.ToString())).FirstOrDefault();

    public IReadOnlyList<ServiceConnection> List(Guid sessionId) =>
        Query("SELECT json FROM connections WHERE session_id = $session", ("$session", sessionId.ToString()));

    public void Upsert(ServiceConnection value) => Execute("""
        INSERT INTO connections (session_id, service, external_user, json) VALUES ($session, $service, $user, $json)
        ON CONFLICT(session_id, service) DO UPDATE SET external_user = excluded.external_user, json = excluded.json
        """, ("$session", value.SessionId.ToString()), ("$service", value.Service.ToString()),
        ("$user", Hash(value.Tokens.ExternalUserId)), ("$json", protector.Protect(JsonSerializer.Serialize(value, Json))));

    public void Delete(Guid sessionId, ServiceKind service) =>
        Execute("DELETE FROM connections WHERE session_id = $session AND service = $service",
            ("$session", sessionId.ToString()), ("$service", service.ToString()));

    public int DeleteByExternalUser(ServiceKind service, string externalUserId)
    {
        var parameters = new[] { ("$service", (object?)service.ToString()), ("$user", Hash(externalUserId)) };
        lock (gate)
        {
            Execute("""
                DELETE FROM sent_routes WHERE service = $service
                    AND session_id IN (SELECT session_id FROM connections WHERE service = $service AND external_user = $user)
                """, parameters);
            return Execute("DELETE FROM connections WHERE service = $service AND external_user = $user", parameters);
        }
    }

    // Course ids and links are not secrets, so unlike tokens they are plain columns.
    public void RecordSent(Guid sessionId, SentRouteRecord sent) => Execute("""
        INSERT INTO sent_routes (session_id, service, route_id, external_id, view_url, sent_at)
        VALUES ($session, $service, $route, $external, $view, $at)
        ON CONFLICT(session_id, service, route_id) DO UPDATE SET
            external_id = excluded.external_id, view_url = excluded.view_url, sent_at = excluded.sent_at
        """, ("$session", sessionId.ToString()), ("$service", sent.Service.ToString()), ("$route", sent.RouteId.ToString()),
        ("$external", sent.Sent.ExternalId), ("$view", sent.Sent.ViewUrl?.ToString()), ("$at", sent.SentAt.ToString("O")));

    public IReadOnlyList<SentRouteRecord> ListSent(Guid sessionId)
    {
        lock (gate)
        {
            using var command = Command("SELECT route_id, service, external_id, view_url, sent_at FROM sent_routes WHERE session_id = $session ORDER BY sent_at",
                [("$session", sessionId.ToString())]);
            using var reader = command.ExecuteReader();
            var results = new List<SentRouteRecord>();
            while (reader.Read())
                if (Enum.TryParse<ServiceKind>(reader.GetString(1), out var service))
                    results.Add(new(Guid.Parse(reader.GetString(0)), service,
                        new(reader.GetString(2), reader.IsDBNull(3) ? null : new Uri(reader.GetString(3))),
                        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            return results;
        }
    }

    public void MoveSent(Guid fromSessionId, Guid toSessionId) =>
        Execute("UPDATE sent_routes SET session_id = $to WHERE session_id = $from",
            ("$from", fromSessionId.ToString()), ("$to", toSessionId.ToString()));

    public void DeleteSent(Guid sessionId, ServiceKind service) =>
        Execute("DELETE FROM sent_routes WHERE session_id = $session AND service = $service",
            ("$session", sessionId.ToString()), ("$service", service.ToString()));

    private List<ServiceConnection> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (gate)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var results = new List<ServiceConnection>();
            while (reader.Read())
            {
                // Rows from lost or rotated-out keys cannot be read: treat them as disconnected.
                try { results.Add(JsonSerializer.Deserialize<ServiceConnection>(protector.Unprotect(reader.GetString(0)), Json)!); }
                catch (CryptographicException) { }
            }
            return results;
        }
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (gate)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string? Hash(string? value) => value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Dispose() { lock (gate) connection.Dispose(); }
}
