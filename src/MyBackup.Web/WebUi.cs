using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;

namespace MyBackup.Web;

/// <summary>Всё, что веб-интерфейс берёт у службы.</summary>
public sealed class WebUiContext
{
    public required ConfigStore Store { get; init; }
    public required JobQueue Queue { get; init; }
    public required ScheduleInfo Schedule { get; init; }
    public required HistoryStore History { get; init; }
    public required TelegramHub Telegram { get; init; }
    public required WebPasswordStore Password { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public static string Version
    {
        get
        {
            var v = typeof(WebUiContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = v.IndexOf('+');
            return plus < 0 ? v : v[..plus];
        }
    }

    internal LoginThrottle Throttle { get; } = new();
}

public static class WebUi
{
    public const string CsrfHeader = "X-MyBackup";
    internal const string StampClaim = "pwstamp";

    /// <summary>Веб-приложение без привязки к адресам: адреса задаёт <see cref="ConfigureKestrel"/> или тестовый сервер.</summary>
    public static WebApplication Build(WebUiContext ctx, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory, ApplicationName = "MyBackup",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(ctx.LoggerFactory));
        builder.Services.AddSingleton(ctx);

        // Ключи для cookie входа — в папке данных службы, зашифрованы DPAPI компьютера.
        builder.Services.AddDataProtection()
            .SetApplicationName("MyBackup")
            .PersistKeysToFileSystem(new DirectoryInfo(ctx.Store.Current.WebKeysDirectory))
            .ProtectKeysWithDpapi(protectToLocalMachine: true);

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "MyBackup.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = c =>
                {
                    c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = c =>
                {
                    c.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                // После смены пароля все прежние входы недействительны.
                o.Events.OnValidatePrincipal = async c =>
                {
                    var store = c.HttpContext.RequestServices.GetRequiredService<WebUiContext>().Password;
                    if (c.Principal?.FindFirst(StampClaim)?.Value != store.Stamp())
                    {
                        c.RejectPrincipal();
                        await c.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        builder.Services.AddAuthorization();

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        // Если интерфейс слушает только localhost — принимаем только такие имена (защита от DNS rebinding).
        var urls = ctx.Store.Current.Web.Urls;
        builder.Configuration["AllowedHosts"] = urls.Count > 0 && urls.All(IsLoopbackUrl) ? "localhost;127.0.0.1;[::1]" : "*";

        configure?.Invoke(builder);

        var app = builder.Build();
        app.Use(SecurityHeaders);
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(RequireCsrfHeader);
        StaticAssets.Map(app);
        Api.Map(app);
        return app;
    }

    /// <summary>Адреса и сертификат из настроек web.</summary>
    public static void ConfigureKestrel(WebApplicationBuilder builder, WebConfig web)
    {
        builder.WebHost.UseUrls([.. web.Urls]);
        if (!string.IsNullOrWhiteSpace(web.CertificatePath))
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(
                Environment.ExpandEnvironmentVariables(web.CertificatePath), Secrets.Reveal(web.CertificatePassword));
            builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = cert));
        }
    }

    public static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    internal static ClaimsPrincipal CreatePrincipal(WebPasswordStore password) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim(StampClaim, password.Stamp())],
            CookieAuthenticationDefaults.AuthenticationScheme));

    private static async Task SecurityHeaders(HttpContext http, Func<Task> next)
    {
        var h = http.Response.Headers;
        h.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        if (http.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
        await next();
    }

    /// <summary>
    /// Изменяющие запросы — только со своим заголовком: другой сайт не может отправить его без CORS,
    /// который здесь не включён. Вместе с SameSite=Strict это защита от CSRF.
    /// </summary>
    private static async Task RequireCsrfHeader(HttpContext http, Func<Task> next)
    {
        var method = http.Request.Method;
        if (http.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)
            && http.Request.Headers[CsrfHeader] != "1")
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "запрос без заголовка " + CsrfHeader });
            return;
        }

        await next();
    }
}
