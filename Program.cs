using QwenWeb.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 🧪 Тестовый модуль мониторинга закупок
builder.Services.AddDbContext<QwenWeb.Data.TenderMonitorDbContext>(options =>
    options.UseSqlite("Data Source=monitor_test.db"));

builder.Services.AddSingleton<QwenWeb.Services.TenderMonitorBackgroundService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<QwenWeb.Services.TenderMonitorBackgroundService>());
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