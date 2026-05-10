// File: Program.cs
using QwenWeb.Services.Documents;
using Microsoft.EntityFrameworkCore;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Services;
using System;
using QwenWeb.Services.Tenderplan;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок (RSS)
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite(builder.Configuration["MonitorSettings:DbPath"] ?? "Data Source=monitor_test.db"));

builder.Services.AddSingleton<TenderMonitorBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TenderMonitorBackgroundService>());

// ==============================================
// 🔹 Tenderplan API Integration
builder.Services.Configure<TenderplanSettings>(builder.Configuration.GetSection("Tenderplan"));
builder.Services.AddDbContext<TenderplanDbContext>(options =>
    options.UseSqlite(builder.Configuration["Tenderplan:DbPath"] ?? "Data Source=tenderplan.db"));

builder.Services.AddHttpClient("TenderplanApi", client =>
{
    client.BaseAddress = new Uri("https://tenderplan.ru");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

builder.Services.AddTransient<ITenderSourceProvider, TenderplanApiProvider>();
builder.Services.AddSingleton<TenderplanBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TenderplanBackgroundService>());

// ==============================================
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<QwenWeb.Services.Documents.EisDocumentService>();

var app = builder.Build();

// 🔹 Инициализация БД Tenderplan (создаёт файл и таблицы при первом запуске)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TenderplanDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();