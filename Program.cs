using Microsoft.EntityFrameworkCore;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Models;
using QwenWeb.Services.Documents;
using QwenWeb.Services.Monitoring;
using System;
using System.Linq;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 🔹 1. Настройки мониторинга
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

// 🔹 2. HTTP и БД
builder.Services.AddHttpClient();
builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration["MonitorSettings:DbPath"] ?? "Data Source=monitor_test.db"));


// 🔹 3. Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 🔹 4. Сервисы документов и мониторинга (Singleton)
builder.Services.AddSingleton<EisDocumentService>();
builder.Services.AddSingleton<MonitorProfileManager>();

// 🔹 5. Браузерный пул и сервис обогащения (НОВОЕ)
// IBrowserPool — Singleton (один экземпляр Chromium на всё приложение)

// BrowserEnrichmentService — Scoped (работает с DbContext)

// 🔹 6. Сборка приложения (ВСЕ регистрации ДО этого момента!)
var app = builder.Build();

// 🔹 7. Инициализация БД + Seed + Деактивация профилей + Запуск менеджера
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Seed: тестовый профиль, если таблица пуста
    if (!await db.MonitorProfiles.AnyAsync())
    {
        db.MonitorProfiles.Add(new MonitorProfile
        {
            Name = "Тест: Иркутская область, 44-ФЗ",
            IsActive = true,
            RegionCode = "38000000000",
            LawType = "44",
            PollIntervalMinutes = 5
        });
        await db.SaveChangesAsync();
        logger.LogInformation("🌱 Seed: добавлен тестовый профиль мониторинга");
    }

    // 🔹 Безопасность: деактивировать все профили при старте (пользователь запускает вручную)
    var activeProfiles = await db.MonitorProfiles
        .Where(p => p.IsActive)
        .ToListAsync();

    if (activeProfiles.Count > 0)
    {
        foreach (var profile in activeProfiles)
        {
            profile.IsActive = false;
            profile.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        logger.LogInformation("🔒 Деактивировано {Count} профилей при запуске (требуют ручного старта)", activeProfiles.Count);
    }

    // 🔹 Запуск фоновой задачи мониторинга
    var manager = scope.ServiceProvider.GetRequiredService<MonitorProfileManager>();
    manager.Start();
    logger.LogInformation("🔄 MonitorProfileManager запущен");
}

// 🔹 8. Middleware
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// 🔹 9. Отладочный вывод (опционально, можно удалить в продакшене)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
    var profile = await db.MonitorProfiles.FindAsync(6);

    Console.WriteLine($"[DEBUG] Profile #6 SearchUrl length: {profile?.SearchUrl?.Length ?? 0}");
    if (!string.IsNullOrEmpty(profile?.SearchUrl))
    {
        var preview = profile.SearchUrl.Length > 500
            ? profile.SearchUrl.Substring(0, 500) + "..."
            : profile.SearchUrl;
        Console.WriteLine($"[DEBUG] Preview: {preview}");
        Console.WriteLine($"[DEBUG] Contains customerPlace: {profile.SearchUrl.Contains("customerPlace")}");
    }
    else
    {
        Console.WriteLine("[DEBUG] Profile #6: SearchUrl is null or empty");
    }
}

// 🔹 10. Запуск приложения
await app.RunAsync();