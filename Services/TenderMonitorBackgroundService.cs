using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QwenWeb.Data;
using QwenWeb.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using QwenWeb.Configuration;

namespace QwenWeb.Services;

public class TenderMonitorBackgroundService : BackgroundService
{
    // Fields
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenderMonitorBackgroundService> _logger;
    private readonly MonitorSettings _settings = null!;
    private readonly object _statsLock = new object();
    private int _totalPollsCount;
    private int _newTendersFoundLastRun;
    private DateTime _lastRunTimeUtc = DateTime.MinValue;
    private bool _isErrorLastRun;

    // Properties (Thread-safe)
    public int TotalPollsCount
    {
        get { lock (_statsLock) return _totalPollsCount; }
    }

    public int NewTendersFoundLastRun
    {
        get { lock (_statsLock) return _newTendersFoundLastRun; }
    }

    public DateTime LastRunTimeUtc
    {
        get { lock (_statsLock) return _lastRunTimeUtc; }
    }

    public bool IsErrorLastRun
    {
        get { lock (_statsLock) return _isErrorLastRun; }
    }

    // Constructor
    public TenderMonitorBackgroundService(IServiceScopeFactory scopeFactory, ILogger<TenderMonitorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // Public Methods

    public TenderMonitorBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TenderMonitorBackgroundService> logger,
    MonitorSettings settings)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = settings;
    }

    public async Task RunManualAsync()
    {
        await FetchAndSaveAsync(CancellationToken.None);
    }

    // Protected/Override
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FetchAndSaveAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int interval = _settings.PollIntervalMinutes;
            await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken);
            await FetchAndSaveAsync(stoppingToken);
        }
    }
    private async Task FetchAndSaveAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        TenderMonitorDbContext db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

        try
        {
            // Считаем опрос (потокобезопасно)
            lock (_statsLock)
            {
                _totalPollsCount++;
            }

            // Создаём клиент с заголовками (как в Tenders.razor)
            using HttpClientHandler handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(_settings.HttpClientTimeoutMinutes);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept", "application/rss+xml, application/xml, text/xml, */*");

            // 👇 Попытка запроса с повтором при ошибке
            HttpResponseMessage? response = null;
            int retryCount = 0;
            const int maxRetries = 2;

            while (retryCount <= maxRetries)
            {
                try
                {
                    response = await client.GetAsync(_settings.RssUrl, token);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch
                {
                    // Игнорируем и пробуем снова
                }

                retryCount++;
                if (retryCount <= maxRetries)
                {
                    await Task.Delay(2000, token); // Ждём 2 секунды перед повтором
                }
            }

            // Если все попытки исчерпаны
            if (response == null || !response.IsSuccessStatusCode)
            {
                string status = response?.StatusCode.ToString() ?? "null";
                throw new HttpRequestException($"Failed after {maxRetries} retries: {status}");
            }

            string xmlContent = await response.Content.ReadAsStringAsync(token);

            // 👇 Проверка: если контент не похож на RSS — пропускаем этот цикл
            if (!xmlContent.Contains("<channel>") || !xmlContent.Contains("<item>"))
            {
                _logger.LogWarning("RSS feed content does not look like valid XML. Skipping this cycle.");
                UpdateStats(0, false);
                return;
            }

            // Парсим XML
            XDocument doc = XDocument.Parse(xmlContent);
            XElement? channel = doc.Root?.Element("channel");

            if (channel == null)
            {
                throw new InvalidOperationException("RSS channel not found in feed.");
            }

            List<TenderMonitorRecord> newRecords = new List<TenderMonitorRecord>();
            int foundCount = 0;

            foreach (XElement item in channel.Elements("item"))
            {
                string title = item.Element("title")?.Value ?? string.Empty;
                string? link = item.Element("link")?.Value;
                string? description = item.Element("description")?.Value;
                DateTime? pubDate = null;
                string? pubDateStr = item.Element("pubDate")?.Value;

                if (!string.IsNullOrEmpty(pubDateStr) && DateTime.TryParse(pubDateStr, out DateTime parsedDate))
                {
                    pubDate = parsedDate;
                }

                if (string.IsNullOrEmpty(link))
                {
                    continue;
                }

                // Дедупликация: проверяем, есть ли запись в БД
                bool exists = await db.Tenders.AnyAsync(t => t.Link == link, token);
                if (!exists)
                {
                    TenderMonitorRecord record = new TenderMonitorRecord
                    {
                        Link = link,
                        Title = title,
                        Description = description,
                        PubDate = pubDate
                    };
                    newRecords.Add(record);
                    foundCount++;
                }
            }

            // Сохраняем новые записи
            if (foundCount > 0)
            {
                db.Tenders.AddRange(newRecords);
                await db.SaveChangesAsync(token);
                _logger.LogInformation("Saved {Count} new tenders to monitor DB.", foundCount);
            }
            else
            {
                _logger.LogInformation("No new tenders found in current RSS cycle.");
            }

            // Обновляем статистику (успех)
            UpdateStats(foundCount, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or process RSS feed.");
            // Обновляем статистику (ошибка)
            UpdateStats(0, true);
        }
    }
    private void UpdateStats(int newCount, bool isError)
    {
        lock (_statsLock)
        {
            _newTendersFoundLastRun = newCount;
            _lastRunTimeUtc = DateTime.UtcNow;
            _isErrorLastRun = isError;
        }
    }
}