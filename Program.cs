// File: Program.cs
using QwenWeb.Services.Documents;
using Microsoft.EntityFrameworkCore;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Services;
using System;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок (RSS) — ЕИС
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration["MonitorSettings:DbPath"] ?? "Data Source=monitor_test.db"));

builder.Services.AddSingleton<TenderMonitorBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TenderMonitorBackgroundService>());

// ✅ Razor Components + Interactive Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ✅ EisDocumentService — регистрируем как Singleton (управляет своим HttpClient с сессией)
builder.Services.AddSingleton<QwenWeb.Services.Documents.EisDocumentService>();

var app = builder.Build();

// 🔹 Инициализация БД мониторинга (ЕИС)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();