using QwenWeb.Configuration;
using Microsoft.EntityFrameworkCore;
using QwenWeb.Data;
using QwenWeb.Services;
using QwenWeb.Components;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок
MonitorSettings monitorSettings = new MonitorSettings();
builder.Configuration.GetSection("MonitorSettings").Bind(monitorSettings);
builder.Services.AddSingleton<MonitorSettings>(monitorSettings);

builder.Services.AddDbContext<TenderMonitorDbContext>(options =>
    options.UseSqlite("Data Source=monitor_test.db"));

builder.Services.AddSingleton<TenderMonitorBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TenderMonitorBackgroundService>());
// ==============================================

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Обычная регистрация, без сложных настроек
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery(); // Обязательно для форм
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();