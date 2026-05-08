using QwenWeb.Services.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;
using QwenWeb.Components;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Services;

using Microsoft.Extensions.Logging;

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

// 📎 Регистрация сервиса документов ЕИС
builder.Services.AddHttpClient<QwenWeb.Services.Documents.EisDocumentService>();
// Обычная регистрация, без сложных настроек
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery(); // Обязательно для форм
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();