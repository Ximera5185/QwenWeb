using Microsoft.EntityFrameworkCore;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Models;
using QwenWeb.Services.Monitoring;
using System;
using System.Linq;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок — ЕИС
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

builder.Services.AddHttpClient();

builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration ["MonitorSettings:DbPath"] ?? "Data Source=monitor_test.db"));

// ✅ Razor Components + Interactive Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ✅ EisDocumentService — Singleton (управляет сессией ЕИС)
builder.Services.AddSingleton<QwenWeb.Services.Documents.EisDocumentService>();

// ✅ MonitorProfileManager — Singleton (управляет фоновым циклом профилей)
builder.Services.AddSingleton<MonitorProfileManager>();

// 🔹 Асинхронный Main для поддержки await
var app = builder.Build();

// 🔹 Инициализация БД + Seed + Деактивация профилей при старте + Запуск менеджера
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Seed: добавляем тестовый профиль, если таблица пуста
    if (!await db.MonitorProfiles.AnyAsync())
    {
        db.MonitorProfiles.Add(new MonitorProfile
        {
            Name = "Тест: Иркутская область, 44-ФЗ",
            IsActive = true,  // Seed-профиль активен, но будет деактивирован ниже
            RegionCode = "38000000000",
            LawType = "44",
            PollIntervalMinutes = 5
        });
        await db.SaveChangesAsync();
        logger.LogInformation("🌱 Seed: добавлен тестовый профиль мониторинга");
    }

    // 🔹 НОВОЕ: При запуске приложения все профили деактивируются для безопасности
    // Пользователь должен явно запустить профили через дашборд
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

    // 🔹 Запускаем MonitorProfileManager
    var manager = scope.ServiceProvider.GetRequiredService<MonitorProfileManager>();
    manager.Start();
    logger.LogInformation("🔄 MonitorProfileManager запущен");
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// 🔹 Временный тест: сбор regnumber через браузер (только иркутская область)
// Удалить после проверки
try
{
    // await QwenWeb.Services.Monitoring.BrowserScraperTest.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[TEST ERROR] BrowserScraperTest: {ex.Message}");
}

// 🔹 Отладочный вывод профиля #6 (безопасная версия)
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

// 🔹 Асинхронный запуск приложения
await app.RunAsync();