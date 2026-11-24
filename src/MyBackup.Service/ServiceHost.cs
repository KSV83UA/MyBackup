using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using Serilog;
using Serilog.Events;

namespace MyBackup.Service;

internal static class Logging
{
    public static Serilog.Core.Logger Create(AppConfig config, bool console)
    {
        var lc = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(config.LogDirectory, "mybackup-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60,
                encoding: Encoding.UTF8,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        if (console)
            lc = lc.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        return lc.CreateLogger();
    }
}

/// <summary>Режим службы Windows (или консоли при ручном запуске без команды).</summary>
internal static class ServiceHost
{
    public const string ServiceName = "MyBackup";

    public static async Task<int> RunAsync(string configPath)
    {
        var isService = WindowsServiceHelpers.IsWindowsService();

        // Без файла настроек служба тоже запускается: всё можно заполнить в веб-интерфейсе.
        AppConfig config;
        try
        {
            config = File.Exists(configPath) ? ConfigLoader.Load(configPath) : new AppConfig();
        }
        catch (ConfigException e)
        {
            await using var fallback = Logging.Create(new AppConfig(), console: !isService);
            fallback.Fatal("{Error}", e.Message);
            return 2;
        }

        var log = Logging.Create(config, console: !isService);
        log.Information("MyBackup {Version} запущен ({Mode}), настройки: {Config}{Missing}", Web.WebUiContext.Version,
            isService ? "служба" : "консоль", configPath, File.Exists(configPath) ? "" : " (файла нет — заполните настройки в веб-интерфейсе)");

        var errors = ConfigLoader.Validate(config);
        foreach (var e in errors) log.Warning("Настройки: {Error}", e);
        if (errors.Count > 0) log.Warning("Задачи с ошибками в настройках выполняться не будут, пока их не исправить");

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(log, dispose: true);

        var store = new ConfigStore(configPath, config);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new HistoryStore(config.HistoryFile));
        builder.Services.AddSingleton(new WebPasswordStore(config.WebPasswordFile));
        builder.Services.AddSingleton<JobQueue>();
        builder.Services.AddSingleton<ScheduleInfo>();
        builder.Services.AddSingleton(sp => new TelegramHub(store, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Telegram")));
        builder.Services.AddSingleton<INotifier>(sp => sp.GetRequiredService<TelegramHub>());

        builder.Services.AddHostedService<JobWorker>();
        builder.Services.AddHostedService<SchedulerService>();
        builder.Services.AddHostedService<TelegramBotService>();
        builder.Services.AddHostedService<WebUiHostedService>();

        await builder.Build().RunAsync();
        return 0;
    }
}
