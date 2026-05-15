using Microsoft.EntityFrameworkCore;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Models;
using QwenWeb.Services.Monitoring;
using System;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок (RSS) — ЕИС
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

builder.Services.AddHttpClient();

builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration["MonitorSettings:DbPath"] ?? "Data Source=monitor_test.db"));

// ✅ Razor Components + Interactive Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ✅ EisDocumentService — Singleton (управляет сессией ЕИС)
builder.Services.AddSingleton<QwenWeb.Services.Documents.EisDocumentService>();

// ✅ MonitorProfileManager — Singleton (управляет фоновым циклом профилей)
builder.Services.AddSingleton<MonitorProfileManager>();

// 🔹 Асинхронный Main для поддержки await
var app = builder.Build();

// 🔹 Инициализация БД + Seed тестовых данных + Запуск менеджера
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

    // Seed: добавляем тестовый профиль, если таблица пуста
    if (!db.MonitorProfiles.Any())
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
    }

    // 🔹 Запускаем MonitorProfileManager
    var manager = scope.ServiceProvider.GetRequiredService<MonitorProfileManager>();
    manager.Start();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// 🔹 временный тест: сбор regnumber через браузер (только иркутская область)
// удалить после проверки
try
{
   //await QwenWeb.Services.Monitoring.BrowserScraperTest.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[TEST ERROR] BrowserScraperTest: {ex.Message}");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
    var profile = await db.MonitorProfiles.FindAsync(6);
    Console.WriteLine($"[DEBUG] Profile #6 SearchUrl length: {profile?.SearchUrl?.Length ?? 0}");
    Console.WriteLine($"[DEBUG] Preview: {profile?.SearchUrl?.Substring(0, Math.Min(500, profile.SearchUrl.Length))}");
    Console.WriteLine($"[DEBUG] Contains customerPlace: {profile?.SearchUrl?.Contains("customerPlace")}");
}
// 🔹 Асинхронный запуск приложения
await app.RunAsync();