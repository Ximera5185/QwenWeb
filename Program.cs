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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();


// 🔹 Тест Этапа 2: получение списка документов

// 🔹 Тест Этапа 2: получение списка документов

// 🔹 Тест мини-Этапа 2.1: извлечение noticeInfoId

// 🔹 Тест мини-Этапа 2.2: получение списка документов

using var scope = app.Services.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<QwenWeb.Data.TenderMonitorDbContext>();
var firstRecord = await dbContext.Tenders.FirstOrDefaultAsync(t => !string.IsNullOrEmpty(t.Link));

if (firstRecord != null)
{
    ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    ILogger<EisDocumentService> logger = loggerFactory.CreateLogger<EisDocumentService>();
    HttpClient httpClient = new HttpClient();

    EisDocumentService testService = new EisDocumentService(httpClient, logger);

    Console.WriteLine($"\n🔍 Тестируем для закупки: {firstRecord.Title}");
    Console.WriteLine($"🔗 Ссылка: {firstRecord.Link}\n");

    // 1. Сначала получаем noticeInfoId
    QwenWeb.Models.NoticeInfoResult? noticeInfo = await testService.GetNoticeInfoIdAsync(firstRecord.Link);

    if (noticeInfo == null || !noticeInfo.IsValid)
    {
        Console.WriteLine("⚠️ Не удалось получить noticeInfoId");
    }
    else
    {
        Console.WriteLine($"✅ noticeInfoId={noticeInfo.NoticeInfoId}, lawType={noticeInfo.LawType}");

        // 2. Получаем список документов
        List<QwenWeb.Models.EisDocumentItem> docs = await testService.GetDocumentListAsync(firstRecord.Link);

        Console.WriteLine($"\n📄 Найдено документов: {docs.Count}");
        foreach (QwenWeb.Models.EisDocumentItem doc in docs)
        {
            Console.WriteLine($"  • {doc.FileName} | ID: {doc.FileId} | ZIP: {doc.IsArchive}");
            Console.WriteLine($"    URL: {doc.DownloadUrl}");
        }
    }
    Console.WriteLine();
}
else
{
    Console.WriteLine("⚠️ База пуста, нечего тестировать");
}
app.Run();