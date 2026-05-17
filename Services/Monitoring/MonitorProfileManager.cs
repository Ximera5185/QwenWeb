using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using QwenWeb.Data;
using QwenWeb.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace QwenWeb.Services.Monitoring;

public class MonitorProfileManager : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitorProfileManager> _logger;
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
        ILogger<MonitorProfileManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Start()
    {
        if (_cts != null && !_cts.IsCancellationRequested) return;
        _cts = new CancellationTokenSource();
        _backgroundTask = RunAsync(_cts.Token);
        _logger.LogInformation("🔄 MonitorProfileManager запущен (Deep Scraping)");
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
        var profile = await db.MonitorProfiles.FindAsync(new object[] { profileId }, ct);
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
        var profile = await db.MonitorProfiles.FindAsync(new object[] { profileId }, ct);
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
                activeProfiles = await db.MonitorProfiles
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.Id)
                    .ToListAsync(token);
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
        if (profile.LastRunAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - profile.LastRunAt.Value;
            var interval = TimeSpan.FromMinutes(profile.PollIntervalMinutes);
            if (elapsed < interval) return;
        }

        if (!string.IsNullOrWhiteSpace(profile.SearchUrl))
        {
            _logger.LogInformation("🌐 [{Name}] → глубокий парсинг (Deep Scraping)", profile.Name);
            await PollProfileDeepAsync(profile, token);
            return;
        }

        _logger.LogWarning("⚠️ [{Name}] пропущен: нет SearchUrl", profile.Name);
    }

    /// <summary>
    /// Глубокий парсинг: заходит на страницу поиска, находит ссылки, переходит по каждой, собирает данные.
    /// </summary>
    private async Task PollProfileDeepAsync(MonitorProfile profile, CancellationToken token)
    {
        string targetDate = profile.UseCustomDate && profile.CustomDate.HasValue
            ? profile.CustomDate.Value.ToString("dd.MM.yyyy")
            : DateTime.Today.ToString("dd.MM.yyyy");

        var searchUrl = ReplaceDateInUrl(profile.SearchUrl, targetDate);
        int totalNewRecords = 0;

        // 🔹 Инициализация браузера
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true, // 🔹 Продакшен: true; Отладка: false
            Args = new[] {
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage"
            }
        });

        var context = await browser.NewContextAsync(new()
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new() { Width = 1280, Height = 900 }
        });

        try
        {
            int maxPages = 3;
            int currentPage = 1;

            while (currentPage <= maxPages && !token.IsCancellationRequested)
            {
                var pageUrl = searchUrl.Contains("pageNumber=")
                    ? Regex.Replace(searchUrl, @"pageNumber=\d+", $"pageNumber={currentPage}")
                    : $"{searchUrl}&pageNumber={currentPage}";

                _logger.LogDebug("📄 [{Name}] Страница {Page}: {Url}", profile.Name, currentPage, pageUrl);

                var searchPage = await context.NewPageAsync();
                var hrefs = new List<string>();

                try
                {
                    await searchPage.GotoAsync(pageUrl, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 45000 });

                    try
                    {
                        await searchPage.WaitForSelectorAsync(".registry-entry__form, a[href*='common-info.html']", new() { Timeout = 15000 });
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("⏳ [{Name}] Карточки закупок не найдены на стр. {Page}. Возможна капча или пустая выдача.", profile.Name, currentPage);
                        var pageContent = await searchPage.ContentAsync();
                        if (pageContent.Contains("captcha", StringComparison.OrdinalIgnoreCase) || pageContent.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("⚠️ [{Name}] Обнаружена капча или блокировка", profile.Name);
                        }
                        break;
                    }

                    // 🔹 Собираем ссылки на страницы деталей (фильтруем по common-info.html)
                    var detailLinks = await searchPage.QuerySelectorAllAsync("a[href*='regNumber=']");
                    _logger.LogDebug("🔗 [{Name}] Найдено потенциальных ссылок: {Count}", profile.Name, detailLinks.Count());

                    foreach (var link in detailLinks)
                    {
                        var href = await link.GetAttributeAsync("href");
                        if (string.IsNullOrEmpty(href)) continue;

                        // 🔹 Фильтруем только ссылки на common-info.html
                        if (!href.Contains("common-info.html", StringComparison.OrdinalIgnoreCase)) continue;

                        // 🔹 Нормализуем относительные ссылки в абсолютные
                        if (href.StartsWith("/"))
                            href = "https://zakupki.gov.ru" + href;
                        else if (!href.StartsWith("http"))
                            href = "https://zakupki.gov.ru/" + href;

                        if (href.Contains("zakupki.gov.ru") && href.Contains("regNumber="))
                            hrefs.Add(href);
                    }

                    _logger.LogInformation("✅ [{Name}] Собрано {Count} ссылок на детали для парсинга", profile.Name, hrefs.Count);

                    // Закрываем страницу поиска
                    await searchPage.CloseAsync();

                    // 🔹 ОБРАБОТКА КАЖДОЙ ЗАКУПКИ
                    foreach (var detailUrl in hrefs)
                    {
                        if (token.IsCancellationRequested) break;

                        try
                        {
                            var tenderPage = await context.NewPageAsync();
                            try
                            {
                                _logger.LogDebug("🔍 [{Name}] Загрузка деталей: {Url}", profile.Name, detailUrl);
                                await tenderPage.GotoAsync(detailUrl, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

                                // 🔹 Ждём появления контента карточки
                                await tenderPage.WaitForSelectorAsync(".cardMainInfo__title, .navBreadcrumb", new() { Timeout = 10000 });

                                var content = await tenderPage.ContentAsync();
                                var record = ParseTenderDetails(content, detailUrl, profile);

                                if (record != null)
                                {
                                    using var scope = _scopeFactory.CreateScope();
                                    var db = scope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();

                                    var exists = await db.Tenders.AnyAsync(t => t.RegNumber == record.RegNumber && t.ProfileId == profile.Id, token);
                                    if (!exists)
                                    {
                                        db.Tenders.Add(record);
                                        await db.SaveChangesAsync(token);
                                        totalNewRecords++;
                                        _logger.LogInformation("✅ Найдена закупка: {RegNumber} - {Title}", record.RegNumber, record.Title);
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ [{Name}] Не удалось распарсить детали для {Url}", profile.Name, detailUrl);
                                }
                            }
                            finally
                            {
                                await tenderPage.CloseAsync();
                            }

                            await Task.Delay(1000, token); // Пауза между запросами
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ Ошибка при парсинге деталей {Url}", detailUrl);
                        }
                    }
                }
                finally
                {
                    if (!searchPage.IsClosed)
                        await searchPage.CloseAsync();
                }

                // Если ссылок мало — вероятно, это последняя страница
                if (hrefs.Count < 10) break;
                currentPage++;
            }
        }
        finally
        {
            await browser.CloseAsync();
        }

        // 🔹 Обновляем статистику профиля
        using var finalScope = _scopeFactory.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TenderMonitorDbContext>();
        var trackedProfile = await finalDb.MonitorProfiles.FindAsync(profile.Id);
        if (trackedProfile != null)
        {
            trackedProfile.LastRunAt = DateTime.UtcNow;
            trackedProfile.LastFoundCount = totalNewRecords;
            trackedProfile.LastError = null;
            trackedProfile.UpdatedAt = DateTime.UtcNow;
            await finalDb.SaveChangesAsync(token);
        }

        UpdateStat(profile.Id, lastRunAt: DateTime.UtcNow, foundCount: totalNewRecords);
        _logger.LogInformation("🎉 [{Name}] Готово. Найдено новых: {Count}", profile.Name, totalNewRecords);
    }

    /// <summary>
    /// Извлекает данные тендера из HTML страницы деталей.
    /// </summary>
    private  TenderMonitorRecord? ParseTenderDetails(string html, string sourceUrl, MonitorProfile profile)
    {
        try
        {
            // 🔹 1. Извлечение RegNumber — несколько стратегий
            string? regNumber = null;

            // Стратегия 1: хлебные крошки (с учётом пробелов)
            var regMatch = Regex.Match(html,
                @"navBreadcrumb__text[^>]*>\s*№\s*([0-9A-Za-z\-_]{10,})",
                RegexOptions.IgnoreCase);
            if (regMatch.Success)
            {
                regNumber = regMatch.Groups[1].Value.Trim();
            }

            // Стратегия 2: из самого URL (фоллбэк)
            if (string.IsNullOrEmpty(regNumber))
            {
                var urlMatch = Regex.Match(sourceUrl, @"regNumber=([0-9A-Za-z\-_]{10,})", RegexOptions.IgnoreCase);
                if (urlMatch.Success)
                {
                    regNumber = urlMatch.Groups[1].Value.Trim();
                }
            }

            // Стратегия 3: поиск в тексте страницы (универсальный)
            if (string.IsNullOrEmpty(regNumber))
            {
                var textMatch = Regex.Match(html,
                    @"(?:номер|№|regNumber)[^0-9]*([0-9]{10,})",
                    RegexOptions.IgnoreCase);
                if (textMatch.Success)
                {
                    regNumber = textMatch.Groups[1].Value.Trim();
                }
            }

            if (string.IsNullOrEmpty(regNumber))
            {
                // 🔹 Отладка: логируем через _logger
                var preview = html.Length > 2000 ? html.Substring(0, 2000) : html;
                _logger.LogDebug("[DEBUG] Не найден regNumber. URL: {Url}", sourceUrl);
                _logger.LogDebug("[DEBUG] HTML preview: {Preview}", preview.Replace("\n", " ").Replace("\r", " ").Substring(0, Math.Min(500, preview.Length)));
                return null;
            }

            // 🔹 2. Заголовок (ObjectName) — несколько вариантов
            string title = "Без названия";

            // Вариант 1: стандартная структура
            var titleMatch = Regex.Match(html,
                @"<span[^>]*class\s*=\s*[""']?cardMainInfo__title[^>]*>Объект закупки</span>\s*<span[^>]*class\s*=\s*[""']?cardMainInfo__content[^>]*>([^<]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                title = CleanText(titleMatch.Groups[1].Value);
            }
            else
            {
                // Вариант 2: упрощённый поиск
                var simpleMatch = Regex.Match(html,
                    @"Объект закупки[^<]*</span>\s*[^>]*>([^<]{10,})",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (simpleMatch.Success)
                {
                    title = CleanText(simpleMatch.Groups[1].Value);
                }
            }

            // 🔹 3. Заказчик
            string customer = string.Empty;
            var custMatch = Regex.Match(html,
                @"(?:Организация,\s*осуществляющая\s*размещение|Заказчик)[^<]*</span>\s*<span[^>]*class\s*=\s*[""']?cardMainInfo__content[^>]*>([^<]+)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (custMatch.Success)
            {
                customer = CleanText(custMatch.Groups[1].Value);
            }

            // 🔹 4. Цена
            decimal? price = null;
            var priceMatch = Regex.Match(html,
                @"(?:Начальная цена|НМЦК|цена)[^<]*</span>\s*<span[^>]*class\s*=\s*[""']?cardMainInfo__content[^>]*cost[^>]*>([\d\s.,]+)\s*&#8381;",
                RegexOptions.IgnoreCase);
            if (priceMatch.Success)
            {
                var raw = priceMatch.Groups[1].Value.Replace(" ", "").Replace(",", ".");
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var p))
                    price = p;
            }

            // 🔹 5. Дата размещения
            DateTime? pubDate = null;
            var dateMatch = Regex.Match(html,
                @"Размещено[^<]*</span>\s*<span[^>]*class\s*=\s*[""']?cardMainInfo__content[^>]*>(\d{2}\.\d{2}\.\d{4})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (dateMatch.Success && DateTime.TryParseExact(
                dateMatch.Groups[1].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                pubDate = d;
            }

            // 🔹 6. Закон (LawType) — из URL или контента
            string lawType = profile.LawType ?? "44";
            if (sourceUrl.Contains("notice223", StringComparison.OrdinalIgnoreCase) || html.Contains("223-ФЗ"))
                lawType = "223";
            else if (sourceUrl.Contains("notice615", StringComparison.OrdinalIgnoreCase) || html.Contains("615-ПП"))
                lawType = "615";
            else if (sourceUrl.Contains("ea20", StringComparison.OrdinalIgnoreCase))
                lawType = "44";

            return new TenderMonitorRecord
            {
                RegNumber = regNumber,
                Link = sourceUrl,
                Title = title,
                Description = html,
                CustomerName = customer,
                InitialPrice = price,
                PubDate = pubDate,
                LawType = lawType,
                RegionCode = profile.RegionCode,
                ProfileId = profile.Id,
                Status = "enriched",
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Ошибка в ParseTenderDetails для {Url}", sourceUrl);
            return null;
        }
    }

    /// <summary>
    /// Очищает текст от лишних пробелов и переносов.
    /// </summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static string ReplaceDateInUrl(string url, string targetDate)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var regexFrom = new Regex(@"publishDateFrom=\d{2}\.\d{2}\.\d{4}", RegexOptions.IgnoreCase);
        url = regexFrom.Replace(url, $"publishDateFrom={targetDate}");
        var regexTo = new Regex(@"publishDateTo=\d{2}\.\d{2}\.\d{4}", RegexOptions.IgnoreCase);
        url = regexTo.Replace(url, $"publishDateTo={targetDate}");
        return url;
    }

    private void UpdateStat(int profileId, DateTime? lastRunAt = null, int? foundCount = null, string? error = null)
    {
        lock (_statsLock)
        {
            if (!_stats.TryGetValue(profileId, out var stats))
                stats = new ProfileStats(true, null, 0, null);
            _stats[profileId] = stats with
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
                _stats[k] = _stats[k] with { IsActive = isActive };
        }
    }

    public void Dispose()
    {
        try { StopAsync().Wait(1000); } catch { }
    }
}