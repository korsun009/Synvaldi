using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Synvaldi;

public sealed class ApiError(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public static ApiError Bad(string code) => new(400, code);
    public static ApiError Conflict(string code) => new(409, code);
    public static ApiError Missing() => new(404, "not_found");
    public static ApiError Denied() => new(401, "invalid_session");
}

public record Actor(Guid AccountId, Guid DeviceId, string DeviceName, byte[] TokenHash);
public record PublicEndpoint;
public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;
    public static string Label(string? value, int max = 128)
    {
        var name = value?.Trim().Normalize();
        if (string.IsNullOrEmpty(name) || name.Length > max || name.Any(char.IsControl))
            throw ApiError.Bad("invalid_name");
        return name;
    }
    public static Actor Actor(this HttpContext context) => (Actor)context.Items[typeof(Actor)]!;
    public static byte[] Hash(string input) => SHA256.HashData(Encoding.UTF8.GetBytes(input));
}

public sealed class Store(NpgsqlDataSource db)
{
    public Task<NpgsqlConnection> Open(CancellationToken ct) => db.OpenConnectionAsync(ct).AsTask();
    public static NpgsqlCommand Sql(NpgsqlConnection conn, string sql, params object[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        return cmd;
    }
    public async Task<T> Write<T>(Func<NpgsqlConnection, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var result = await work(conn);
        await tx.CommitAsync(ct);
        return result;
    }
    public async Task Init(CancellationToken ct)
    {
        using var reader = new StreamReader(typeof(Store).Assembly.GetManifestResourceStream("Synvaldi.Server.schema.sql")!);
        var schema = await reader.ReadToEndAsync(ct);
        await Write(async conn =>
        {
            await Exec(conn, "SELECT pg_advisory_xact_lock(753156332)", ct);
            await Exec(conn, schema, ct);
            return 0;
        }, ct);
    }
    public static async Task<int> Exec(NpgsqlConnection conn, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = Sql(conn, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    public static async Task<List<T>> Rows<T>(NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> map,
        CancellationToken ct, params object[] args)
    {
        await using var cmd = Sql(conn, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct)) result.Add(map(reader));
        return result;
    }
}

public sealed class Retention(Store store, ILogger<Retention> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                await using var conn = await store.Open(stoppingToken);
                await Store.Exec(conn, "DELETE FROM revision WHERE created_at < now() - interval '72 hours'", stoppingToken);
                await Store.Exec(conn, "DELETE FROM session WHERE expires_at <= now()", stoppingToken);
                await Store.Exec(conn, "DELETE FROM online_lease WHERE expires_at <= now()", stoppingToken);
                await Store.Exec(conn, "DELETE FROM trash WHERE created_at < now() - interval '30 days'", stoppingToken);
            }
            catch (NpgsqlException e)
            {
                log.LogWarning("History cleanup temporarily unavailable ({Type})", e.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
