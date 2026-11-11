using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using Synvaldi;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
var dbConfig = new NpgsqlConnectionStringBuilder(
    builder.Configuration["SYNVALDI_DB"] ?? throw new InvalidOperationException("Set SYNVALDI_DB."));
if (builder.Configuration["SYNVALDI_DB_PASSWORD_FILE"] is string path)
    dbConfig.Password = File.ReadAllText(path).TrimEnd('\n', '\r');
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(dbConfig.ToString()));
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Push>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Push>());
builder.Services.AddHostedService<Retention>();
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = 429;
    limits.AddPolicy("login", http => RateLimitPartition.GetSlidingWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "local",
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6
        }));
});
var app = builder.Build();
await app.Services.GetRequiredService<Store>().Init(app.Lifetime.ApplicationStopping);
app.UseRouting();
app.UseRateLimiter();
app.Use(async (http, next) =>
{
    try
    {
        if (http.GetEndpoint() is not null && http.GetEndpoint()?.Metadata.GetMetadata<PublicEndpoint>() is null)
        {
            http.Items[typeof(Actor)] = await Accounts.Authenticate(http, http.RequestServices.GetRequiredService<Store>());
            http.Response.Headers.CacheControl = "no-store";
        }
        await next();
    }
    catch (ApiError e) when (!http.Response.HasStarted)
    {
        http.Response.StatusCode = e.Status;
        await http.Response.WriteAsJsonAsync(new { error = e.Message });
    }
    catch (PostgresException e) when (!http.Response.HasStarted && e.SqlState is "22P05" or "22021" or "22003")
    {
        http.Response.StatusCode = 400;
        await http.Response.WriteAsJsonAsync(new { error = "invalid_value" });
    }
});
app.MapGet("/health", async (Store store, CancellationToken ct) =>
{
    await using var conn = await store.Open(ct);
    await Store.Exec(conn, "SELECT 1", ct);
    return Results.Ok(new { status = "ok" });
}).WithMetadata(new PublicEndpoint());
var api = app.MapGroup("/api/v1");
Accounts.Map(api);
Profiles.Map(api);
Content.Map(api);
app.Run();
