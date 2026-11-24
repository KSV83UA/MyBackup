using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyBackup.Web;

/// <summary>Журнал веб-сервера пишется туда же, куда и журнал службы.</summary>
internal sealed class ForwardingLoggerProvider(ILoggerFactory factory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

    public void Dispose()
    {
    }
}

/// <summary>Файлы интерфейса из ресурсов сборки.</summary>
internal static class StaticAssets
{
    private sealed record Asset(byte[] Content, string ContentType, string ETag);

    private static readonly Lazy<Dictionary<string, Asset>> Assets = new(Load);

    public static IReadOnlyCollection<string> Names => Assets.Value.Keys;

    public static void Map(WebApplication app)
    {
        foreach (var (name, asset) in Assets.Value)
        {
            var route = name == "index.html" ? "/" : "/" + name;
            app.MapGet(route, (HttpContext http) => Serve(http, asset)).AllowAnonymous();
        }
    }

    private static IResult Serve(HttpContext http, Asset asset)
    {
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.ETag = asset.ETag;
        if (http.Request.Headers.IfNoneMatch == asset.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(asset.Content, asset.ContentType);
    }

    private static Dictionary<string, Asset> Load()
    {
        var assembly = typeof(StaticAssets).Assembly;
        var result = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            var name = resource["wwwroot/".Length..];
            var type = Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream",
            };
            result[name] = new Asset(bytes, type, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"");
        }

        return result;
    }
}

/// <summary>Защита от подбора пароля: после 5 ошибок с одного адреса — растущая пауза (до 15 минут).</summary>
internal sealed class LoginThrottle(TimeProvider? time = null)
{
    private const int FreeAttempts = 5;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _state = new();

    public bool CanTry(string key, out int waitSeconds)
    {
        waitSeconds = 0;
        if (!_state.TryGetValue(key, out var s) || s.Until <= _time.GetUtcNow()) return true;
        waitSeconds = (int)Math.Ceiling((s.Until - _time.GetUtcNow()).TotalSeconds);
        return false;
    }

    public void Failed(string key) =>
        _state.AddOrUpdate(key, _ => (1, DateTimeOffset.MinValue), (_, s) =>
        {
            var fails = s.Fails + 1;
            if (fails < FreeAttempts) return (fails, DateTimeOffset.MinValue);
            var seconds = Math.Min(900, 30 * Math.Pow(2, fails - FreeAttempts));
            return (fails, _time.GetUtcNow().AddSeconds(seconds));
        });

    public void Succeeded(string key) => _state.TryRemove(key, out _);
}
