using System.Text.Json;
using System.Text.Json.Serialization;
using Meander.Core;
using Microsoft.Data.Sqlite;

namespace Meander.Infrastructure;

public sealed class ResultStorageOptions
{
    public string DatabasePath { get; set; } = "data/meander.sqlite";
}

// A saved search is immutable: opening its URL always returns the same geometries.
// Keep the provider's full draft as well so a saved drawn section can be edited/reused.
public sealed class SqliteRouteSearchStore : IRouteSearchStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly SqliteConnection connection;
    private readonly TimeProvider clock;
    private readonly object gate = new();

    public SqliteRouteSearchStore(ResultStorageOptions options, TimeProvider clock)
    {
        this.clock = clock;
        if (string.IsNullOrWhiteSpace(options.DatabasePath)) throw new InvalidOperationException("ResultStorage:DatabasePath is required.");
        if (options.DatabasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS searches (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS candidates (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS drafts (id TEXT PRIMARY KEY, json TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public SavedRouteSearch Save(RouteRequest request, IReadOnlyList<RouteCandidate> candidates,
        RouteDraft? draft = null, RouteCandidate? preview = null)
    {
        if (candidates.Count == 0) throw new ArgumentException("A saved search must contain routes.", nameof(candidates));
        var id = Guid.NewGuid();
        var routes = candidates.Select(c => c with { SearchId = id, ExpiresAt = null }).ToArray();
        var savedPreview = preview is null ? null : preview with { ExpiresAt = null };
        var search = new SavedRouteSearch(id, clock.GetUtcNow(), request, routes,
            draft?.RequestedPoints ?? [], savedPreview);
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            Write("searches", id, search, transaction);
            foreach (var route in routes) Write("candidates", route.Id, route, transaction);
            if (request.DrawnRouteId is { } draftId && draft is not null)
            {
                Write("drafts", draftId, draft, transaction);
                if (savedPreview is not null) Write("candidates", draftId, savedPreview, transaction);
            }
            transaction.Commit();
        }
        return search;
    }

    public SavedRouteSearch? Get(Guid id) => Read<SavedRouteSearch>("searches", id);
    public RouteCandidate? GetCandidate(Guid id) => Read<RouteCandidate>("candidates", id);
    public RouteDraft? GetDraft(Guid id) => Read<RouteDraft>("drafts", id);

    private T? Read<T>(string table, Guid id)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            // The table names are internal constants; user input is always parameterized.
            command.CommandText = $"SELECT json FROM {table} WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
        }
    }

    private void Write<T>(string table, Guid id, T value, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} (id, json) VALUES ($id, $json) ON CONFLICT(id) DO NOTHING";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Json));
        command.ExecuteNonQuery();
    }

    public void Dispose() { lock (gate) connection.Dispose(); }
}
