using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using QwenWeb.Data;
using QwenWeb.Models;
using QwenWeb.Services.Documents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QwenWeb.Services.Monitoring;

public class MonitorProfileManager : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitorProfileManager> _logger;
    private readonly EisDocumentService _eisService;
    private readonly Random _random = new();
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    private readonly Dictionary<int, ProfileStats> _stats = new();
    private readonly object _statsLock = new();

    public record ProfileStats(
        bool IsActive,
        DateTime? LastRunAt,
        int LastFoundCount,
        string? LastError
    );

    public MonitorProfileManager(
        IServiceScopeFactory scopeFactory,
        ILogger<MonitorProfileManager> logger,
        EisDocumentService eisService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _eisService = eisService;
    }

    public void Start()
    {
        if (_cts != null && !_cts.IsCancellationRequested) return;
        _cts = new CancellationTokenSource();
        _backgroundTask = RunAsync(_cts.Token);
        _logger.LogInformation("🔄 MonitorProfileManager запущен");
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { await (_backgroundTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        _backgroundTask = null;
        UpdateAllStats(isActive: false);
        _logger.LogInformation("🛑 MonitorProfileManager остановлен");
    }

    public IReadOnlyDictionary<int, ProfileStats> GetStats()
    {
        lock (_statsLock)
            return _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task ToggleProfileAsync(int profileId, bool activate, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

        var profile = await db.MonitorProfiles.FindAsync(new object [] { profileId }, ct);
        if (profile == null) throw new InvalidOperationException($"Профиль {profileId} не найден");

        profile.IsActive = activate;
        profile.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("🔄 Профиль {Id} {Status}", profileId, activate ? "активирован" : "деактивирован");
    }

    public async Task DeleteProfileAsync(int profileId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

        var profile = await db.MonitorProfiles.FindAsync(new object [] { profileId }, ct);
        if (profile == null) throw new InvalidOperationException($"Профиль {profileId} не найден");

        db.MonitorProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);

        lock (_statsLock) _stats.Remove(profileId);
        _logger.LogInformation("🗑 Профиль {Id} удалён из БД", profileId);
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            List<MonitorProfile> activeProfiles;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

                _logger.LogDebug("🔄 загрузка активных профилей из бд...");
                activeProfiles = await db.MonitorProfiles
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.Id)
                    .ToListAsync(token);

                _logger.LogInformation("📊 загружено {Count} активных профилей", activeProfiles.Count);
                foreach (var p in activeProfiles)
                {
                    _logger.LogDebug("  - профиль #{Id} («{Name}»), SearchUrl длина={Len}",
                        p.Id, p.Name, p.SearchUrl?.Length ?? 0);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Ошибка загрузки профилей");
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                continue;
            }

            if (activeProfiles.Count == 0)
            {
                UpdateAllStats(isActive: true);
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                continue;
            }

            foreach (var profile in activeProfiles)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    await PollProfileAsync(profile, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "💥 Ошибка опроса профиля {ProfileId} ({Name})", profile.Id, profile.Name);
                    UpdateStat(profile.Id, error: ex.Message);
                }

                var delay = TimeSpan.FromSeconds(_random.Next(3, 6));
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task PollProfileAsync(MonitorProfile profile, CancellationToken token)
    {
        // 🔹 НОВОЕ: проверка интервала опроса
        if (profile.LastRunAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - profile.LastRunAt.Value;
            var interval = TimeSpan.FromMinutes(profile.PollIntervalMinutes);

            if (elapsed < interval)
            {
                var remaining = interval - elapsed;
                _logger.LogDebug("⏳ [{Name}] пропущен: до следующего опроса осталось {Remaining}",
                    profile.Name, remaining.ToString(@"hh\:mm\:ss"));
                return; // ← не запускаем скрапер, ждём следующего цикла
            }
        }

        // 🔹 Исправлено: полная лог-строка с параметрами
        _logger.LogDebug("📋 профиль #{Id} («{Name}»): SearchUrl длина={Len}, RegionCode={Region}, LawType={Law}, IsActive={Active}",
            profile.Id, profile.Name,
            profile.SearchUrl?.Length ?? 0,
            profile.RegionCode ?? "null",
            profile.LawType ?? "null",
            profile.IsActive);

        if (!string.IsNullOrWhiteSpace(profile.SearchUrl))
        {
            _logger.LogInformation("🌐 [{Name}] → браузерный скрапер", profile.Name);
            await PollProfileWithBrowserAsync(profile, token);
            return;
        }

        _logger.LogWarning("⚠️ [{Name}] пропущен: нет SearchUrl", profile.Name);
        UpdateStat(profile.Id, error: "не настроен: пустой SearchUrl");
    }

    private async Task PollProfileWithBrowserAsync(MonitorProfile profile, CancellationToken token)
    {
        _logger.LogInformation("🌐 [{ProfileName}] запуск браузерного скрапера", profile.Name);

        try
        {
            // 🔹 Определяем целевую дату: если UseCustomDate=true и дата задана — используем её
            string targetDate = profile.UseCustomDate && profile.CustomDate.HasValue
                ? profile.CustomDate.Value.ToString("dd.MM.yyyy")
                : DateTime.Today.ToString("dd.MM.yyyy");

            var searchUrl = ReplaceDateInUrl(profile.SearchUrl, targetDate);
            _logger.LogDebug("🔗 [{ProfileName}] обновлённая ссылка: {Url}", profile.Name, searchUrl);

            var regNumbers = await ScrapeRegNumbersAsync(profile, searchUrl, token);
            _logger.LogInformation("✅ [{ProfileName}] найдено regNumber: {Count}", profile.Name, regNumbers.Count);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

            int newCount = 0;
            foreach (var regNumber in regNumbers)
            {
                var exists = await db.Tenders.AnyAsync(t => t.RegNumber == regNumber && t.ProfileId == profile.Id, token);
                if (exists) continue;

                var detailLink = $"https://zakupki.gov.ru/epz/order/notice/view/common-info.html?regNumber={regNumber}";

                var record = new TenderMonitorRecord
                {
                    RegNumber = regNumber,
                    Link = detailLink,
                    Title = $"Закупка #{regNumber}",
                    ProfileId = profile.Id,
                    RegionCode = profile.RegionCode,
                    LawType = profile.LawType,
                    Status = "raw",
                    CreatedAtUtc = DateTime.UtcNow
                };

                db.Tenders.Add(record);
                newCount++;
            }

            if (newCount > 0)
            {
                await db.SaveChangesAsync(token);
                _logger.LogInformation("🎉 [{ProfileName}] сохранено {Count} новых записей", profile.Name, newCount);
            }
            else
            {
                _logger.LogInformation("ℹ️ [{ProfileName}] новых записей не найдено (все дубли или пусто)", profile.Name);
            }

            // 🔹 ИСПРАВЛЕНО: сохраняем LastRunAt и LastFoundCount в БД для работы интервала опроса
            var trackedProfile = await db.MonitorProfiles.FindAsync(new object [] { profile.Id }, token);
            if (trackedProfile != null)
            {
                trackedProfile.LastRunAt = DateTime.UtcNow;
                trackedProfile.LastFoundCount = newCount;
                trackedProfile.LastError = null;
                trackedProfile.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
                _logger.LogDebug("💾 [{Name}] сохранён LastRunAt={LastRunAt}, LastFoundCount={Count}",
                    profile.Name, trackedProfile.LastRunAt, newCount);
            }

            UpdateStat(profile.Id, lastRunAt: DateTime.UtcNow, foundCount: newCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 [{ProfileName}] ошибка в браузерном скрапере", profile.Name);
            UpdateStat(profile.Id, error: ex.Message);

            // 🔹 Также сохраняем ошибку в БД
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
            var trackedProfile = await db.MonitorProfiles.FindAsync(new object [] { profile.Id }, token);
            if (trackedProfile != null)
            {
                trackedProfile.LastError = ex.Message;
                trackedProfile.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
            }
        }
    }

    // 🔹 Исправлено: метод теперь принимает targetDate, а не вычисляет её внутри
    private static string ReplaceDateInUrl(string url, string targetDate)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        var regexFrom = new Regex(@"publishDateFrom=\d{2}\.\d{2}\.\d{4}", RegexOptions.IgnoreCase);
        url = regexFrom.Replace(url, $"publishDateFrom={targetDate}");

        var regexTo = new Regex(@"publishDateTo=\d{2}\.\d{2}\.\d{4}", RegexOptions.IgnoreCase);
        url = regexTo.Replace(url, $"publishDateTo={targetDate}");

        return url;
    }

    // 🔹 Исправлено: метод принимает baseUrl как параметр, а не вычисляет его внутри
    private async Task<HashSet<string>> ScrapeRegNumbersAsync(MonitorProfile profile, string baseUrl, CancellationToken token)
    {
        var regNumbers = new HashSet<string>();
        int maxPages = 3;
        int currentPage = 1;

        // 🔹 Лог: исходная ссылка перед скрапингом
        var preview = baseUrl.Length > 300 ? baseUrl.Substring(0, 300) + "..." : baseUrl;
        _logger.LogInformation("🔍 [{Name}] исходная ссылка (длина {Len}): {Preview}",
            profile.Name, baseUrl.Length, preview);
        _logger.LogDebug("🔍 [{Name}] полная ссылка: {Url}", profile.Name, baseUrl);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = false,
            Args = new [] {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--disable-accelerated-2d-canvas",
                "--disable-gpu"
            }
        });

        var context = await browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new() { Width = 1280, Height = 900 },
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                { "accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8" },
                { "accept-language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7" },
                { "sec-ch-ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"" },
                { "sec-ch-ua-mobile", "?0" },
                { "sec-ch-ua-platform", "\"Windows\"" }
            }
        });

        var page = await context.NewPageAsync();

        try
        {
            while (currentPage <= maxPages && !token.IsCancellationRequested)
            {
                var pageUrl = baseUrl.Contains("pageNumber=")
                    ? Regex.Replace(baseUrl, @"pageNumber=\d+", $"pageNumber={currentPage}")
                    : $"{baseUrl}&pageNumber={currentPage}";

                _logger.LogDebug("📄 [{Name}] страница {Page}: {Url}", profile.Name, currentPage, pageUrl);
                _logger.LogInformation("🔗 [{Name}] страница {Page}: {Url}", profile.Name, currentPage, pageUrl);

                await page.GotoAsync(pageUrl, new()
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 60000
                });

                try
                {
                    await page.WaitForSelectorAsync("a[href*='regNumber=']", new() { Timeout = 10000 });
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("⏳ [{Name}] элементы не найдены на странице {Page}. возможна капча или конец выдачи.", profile.Name, currentPage);
                    break;
                }

                var links = await page.QuerySelectorAllAsync("a[href*='regNumber=']");
                foreach (var link in links)
                {
                    var href = await link.GetAttributeAsync("href");
                    if (string.IsNullOrEmpty(href)) continue;

                    var match = Regex.Match(href, @"regNumber=([0-9A-Za-z\-_]+)");
                    if (match.Success) regNumbers.Add(match.Groups [1].Value);
                }

                _logger.LogDebug("✅ [{Name}] собрано regnumber на стр. {Page}: {Count}", profile.Name, currentPage, links.Count());

                var hasNext = await page.QuerySelectorAsync("a.next-page, .pagination__next, [class*='next']") != null;
                if (!hasNext)
                {
                    _logger.LogDebug("🏁 [{Name}] кнопка 'далее' не найдена. завершаем сбор.", profile.Name);
                    break;
                }

                currentPage++;
                await Task.Delay(2000, token);
            }
        }
        finally
        {
            await browser.CloseAsync();
        }

        return regNumbers;
    }

    private static string [] GetRegionNames(string regionCode) => regionCode switch
    {
        "38000000000" => new [] { "Иркутская область", "Иркутской области", "Иркутская обл.", "г. Иркутск" },
        "24000000000" => new [] { "Красноярский край", "Красноярского края", "г. Красноярск" },
        "54000000000" => new [] { "Новосибирская область", "Новосибирской области", "г. Новосибирск" },
        "27000000000" => new [] { "Хабаровский край", "Хабаровского края", "г. Хабаровск" },
        "28000000000" => new [] { "Амурская область", "Амурской области", "г. Благовещенск" },
        "75000000000" => new [] { "Забайкальский край", "Забайкальского края", "г. Чита" },
        "79000000000" => new [] { "Еврейская автономная область", "ЕАО", "г. Биробиджан" },
        "87000000000" => new [] { "Чукотский автономный округ", "Чукотка", "г. Анадырь" },
        "41000000000" => new [] { "Камчатский край", "Камчатки", "г. Петропавловск-Камчатский" },
        "65000000000" => new [] { "Сахалинская область", "Сахалина", "г. Южно-Сахалинск" },
        "03000000000" => new [] { "Республика Бурятия", "Бурятия", "г. Улан-Удэ" },
        "25000000000" => new [] { "Приморский край", "Приморья", "г. Владивосток" },
        _ => new [] { regionCode }
    };

    private void UpdateStat(int profileId, DateTime? lastRunAt = null, int? foundCount = null, string? error = null)
    {
        lock (_statsLock)
        {
            if (!_stats.TryGetValue(profileId, out var stats))
                stats = new ProfileStats(true, null, 0, null);

            _stats [profileId] = stats with
            {
                LastRunAt = lastRunAt ?? stats.LastRunAt,
                LastFoundCount = foundCount ?? stats.LastFoundCount,
                LastError = error ?? stats.LastError
            };
        }
    }

    private void UpdateAllStats(bool isActive)
    {
        lock (_statsLock)
        {
            var keys = _stats.Keys.ToList();
            foreach (var k in keys)
                _stats [k] = _stats [k] with { IsActive = isActive };
        }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(1000); } catch { }
    }
}