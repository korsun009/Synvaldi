using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Synvaldi;

internal sealed record Account(string Server, string User, string Device, string AccountId, string Token);
internal sealed record Options(string Cloud = "", string Browser = "", bool Tabs = false, bool Paused = false,
    string[]? Workspaces = null, string Downloads = "", string Attachments = "", string Epoch = "", bool Applied = false);
internal sealed record Pending(string Id, string Profile, JsonObject Body);
internal sealed class LocalStore
{
    private readonly string connection;
    private readonly object gate = new();

    public LocalStore(string path)
    {
        Directory.CreateDirectory(path);
        connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(path, "agent.sqlite3") }.ToString();
        With(db => {
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS settings(k TEXT PRIMARY KEY, v TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS outbox(id TEXT PRIMARY KEY, profile TEXT NOT NULL, body TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS backup(id INTEGER PRIMARY KEY, profile TEXT NOT NULL, created TEXT NOT NULL, body TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
            return true;
        });
    }
    private T With<T>(Func<SqliteConnection,T> action)
    {
        lock (gate)
        {
            using var db = new SqliteConnection(connection);
            db.Open();
            return action(db);
        }
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params object[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i]);
        return cmd;
    }
    public string? Get(string key) => With(db => {
        using var cmd = Command(db, "SELECT v FROM settings WHERE k=$p0", key);
        return cmd.ExecuteScalar() as string;
    });
    public void Set(string key, string value) => With(db => {
        using var cmd = Command(db, "INSERT INTO settings VALUES($p0,$p1) ON CONFLICT(k) DO UPDATE SET v=excluded.v", key, value);
        return cmd.ExecuteNonQuery();
    });
    public Account? Login
    {
        get {
            var data = Get("account");
            if (string.IsNullOrEmpty(data)) return null;
            return JsonSerializer.Deserialize<Account>(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        set => Set("account", value == null ? "" : Convert.ToBase64String(ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser)));
    }
    public Options Options
    {
        get => JsonSerializer.Deserialize<Options>(Get("options") ?? "{}") ?? new Options();
        set => Set("options", JsonSerializer.Serialize(value));
    }
    public void Enqueue(string profile, string id, JsonObject data) => With(db => {
        using var cmd = Command(db, "INSERT OR IGNORE INTO outbox(id,profile,body) VALUES($p0,$p1,$p2)", id, profile, data.ToJsonString());
        return cmd.ExecuteNonQuery();
    });
    public Pending? Next(string profile) => With(db => {
        using var cmd = Command(db, "SELECT id,body FROM outbox WHERE done=0 AND profile=$p0 ORDER BY rowid LIMIT 1", profile);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new Pending(reader.GetString(0), profile, JsonNode.Parse(reader.GetString(1))!.AsObject()) : null;
    });
    public void Done(string id) => With(db => {
        using var cmd = Command(db, "UPDATE outbox SET done=1,body='{}' WHERE id=$p0", id);
        return cmd.ExecuteNonQuery();
    });
    public bool Seen(string id) => With(db => {
        using var cmd = Command(db, "SELECT 1 FROM outbox WHERE id=$p0", id);
        return cmd.ExecuteScalar() != null;
    });
    public void Backup(string profile, JsonArray records) => With(db => {
        using var cmd = Command(db, "INSERT INTO backup(profile,created,body) VALUES($p0,$p1,$p2)", profile, DateTime.UtcNow.ToString("o"), records.ToJsonString());
        return cmd.ExecuteNonQuery();
    });
    public JsonArray? BackupFor(string profile) => With(db => {
        using var cmd = Command(db,"SELECT body FROM backup WHERE profile=$p0 ORDER BY id DESC LIMIT 1", profile);
        return cmd.ExecuteScalar() is string data ? JsonNode.Parse(data)!.AsArray() : null;
    });
    public JsonObject Projected(string profile)
    {
        var state=State(profile);
        return With(db=>{
            using var cmd=Command(db,"SELECT body FROM outbox WHERE done=0 AND profile=$p0 ORDER BY rowid",profile);
            using var reader=cmd.ExecuteReader();
            while(reader.Read())
            {
                foreach(var change in JsonNode.Parse(reader.GetString(0))?["changes"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var key=change["key"]!.ToString();
                    state["fields"]![key]=new JsonObject{["value"]=change["value"]?.DeepClone(),["deleted"]=change["deleted"]?.DeepClone(),["revision"]=change["baseRevision"]?.DeepClone()};
                }
            }
            return state;
        });
    }
    public JsonObject State(string profile) => JsonNode.Parse(Get("state/" + profile) ?? "{\"revision\":0,\"fields\":{}}")!.AsObject();
    public void State(string profile, JsonObject state) => Set("state/" + profile, state.ToJsonString());
}
