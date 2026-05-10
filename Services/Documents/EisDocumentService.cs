// File: Services/Documents/EisDocumentService.cs
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using QwenWeb.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PdfPage = UglyToad.PdfPig.Content.Page;

namespace QwenWeb.Services.Documents;

/// <summary>
/// Сервис для работы с документами ЕИС (zakupki.gov.ru).
/// Поддерживает сессию через CookieContainer для обхода защиты 403/капча.
/// </summary>
public class EisDocumentService
{
    private readonly ILogger<EisDocumentService> _logger;
    private readonly string _storageRoot;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _httpClient;

    private const string EisBaseUrl = "https://zakupki.gov.ru";

    // 🔹 Настройки повторов
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    };
    public EisDocumentService(ILogger<EisDocumentService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storageRoot = Path.Combine(AppContext.BaseDirectory, "Storage", "Tenders");

        // 🔹 ЕДИНЫЙ HANDLER С КУКИ (Singleton-логика)
        _handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        // 🔹 ЕДИНЫЙ HTTP-КЛИЕНТ (переиспользуется для всех запросов к ЕИС)
        _httpClient = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
        _httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");
    }

    // 🔹 Этап 2.1: Извлечение noticeInfoId или regNumber
    // 🔹 Этап 2.1: Извлечение noticeInfoId или regNumber (ИСПРАВЛЕНО: LawType из URL)
    public async Task<NoticeInfoResult?> GetNoticeInfoIdAsync(
        string procurementUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🔍 Поиск идентификатора закупки для URL={Url}", procurementUrl);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, procurementUrl);
            request.Headers.Referrer = new Uri(EisBaseUrl);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ ЕИС вернул статус {StatusCode} для URL={Url}", response.StatusCode, procurementUrl);
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("📄 Получено HTML, длина: {Length} символов", html.Length);

            NoticeInfoResult result = ParseNoticeInfo(html);

            // 🔹 ИСПРАВЛЕНИЕ: Если LawType не определён — извлекаем из procurementUrl
            // 🔹 ИСПРАВЛЕНИЕ: Явное определение LawType для 223-ФЗ и других законов
            if (string.IsNullOrEmpty(result.LawType) && !string.IsNullOrEmpty(procurementUrl))
            {
                // 1. Пробуем извлечь из самого procurementUrl
                var lawFromUrl = Regex.Match(procurementUrl, @"/notice/(notice\d{3}|notice44|notice615|zk\d{2}|ea\d{2})/", RegexOptions.IgnoreCase);
                if (lawFromUrl.Success)
                {
                    result.LawType = lawFromUrl.Groups[1].Value.ToLowerInvariant();
                    _logger.LogDebug("✅ LawType извлечён из URL: {LawType}", result.LawType);
                }
                // 2. Если не получилось — ищем в контексте страницы (рядом с ссылкой)
                else if (!string.IsNullOrEmpty(result.DocumentsPageUrl))
                {
                    int linkIndex = html.IndexOf(result.DocumentsPageUrl, StringComparison.OrdinalIgnoreCase);
                    if (linkIndex > 0)
                    {
                        int contextStart = Math.Max(0, linkIndex - 800);
                        string context = html.Substring(contextStart, Math.Min(800, html.Length - contextStart));
                        // Ищем явные маркеры 223-ФЗ, 44-ФЗ, 615-ПП
                        var lawMatch = Regex.Match(context,
                            @"(?:закон|фз|пп)\s*[:\s\-]*\s*(223|44|615)|notice(223|44|615)|law\s*:\s*(223|44|615)",
                            RegexOptions.IgnoreCase);
                        if (lawMatch.Success)
                        {
                            string code = lawMatch.Groups[1].Value + lawMatch.Groups[2].Value + lawMatch.Groups[3].Value;
                            result.LawType = "notice" + code;
                            _logger.LogDebug("✅ LawType определён по контексту: {LawType}", result.LawType);
                        }
                    }
                }
            }

            if (result.IsValid)
            {
                string identifier = !string.IsNullOrEmpty(result.NoticeInfoId)
                    ? $"noticeInfoId={result.NoticeInfoId}"
                    : $"regNumber={result.RegNumber}";
                _logger.LogInformation("✅ Найдено: {Identifier}, lawType={LawType}", identifier, result.LawType);
            }
            else
            {
                _logger.LogWarning("❌ Не удалось извлечь идентификатор: IsValid={IsValid}, NoticeInfoId={NoticeInfoId}, RegNumber={RegNumber}, LawType={LawType}",
                    result.IsValid, result.NoticeInfoId, result.RegNumber, result.LawType);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "💥 Ошибка при поиске идентификатора для URL={Url}", procurementUrl);
            return null;
        }
    }

    // 🔹 Парсинг noticeInfoId или regNumber из HTML
    // 🔹 Парсинг noticeInfoId или regNumber из HTML (ОТЛАДОЧНАЯ ВЕРСИЯ)
    // 🔹 Парсинг noticeInfoId или regNumber из HTML (ОБНОВЛЕНО под новую структуру ЕИС с /view/)
    // 🔹 Парсинг noticeInfoId или regNumber из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг noticeInfoId или regNumber из HTML (УНИВЕРСАЛЬНЫЙ: не зависит от текста внутри <a>)
    // 🔹 Парсинг noticeInfoId или regNumber из HTML (УНИВЕРСАЛЬНЫЙ: не зависит от текста внутри <a>)
    private NoticeInfoResult ParseNoticeInfo(string html)
    {
        var result = new NoticeInfoResult();
        if (string.IsNullOrWhiteSpace(html) || html.Length < 1000) return result;

        // 🔍 ОТЛАДКА: логируем ВСЕ ссылки, содержащие documents.html
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var allDocLinks = Regex.Matches(html,
                @"<a[^>]*href\s*=\s*[""']?([^""'\s>]*documents\.html[^""'\s>]*)[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            _logger.LogDebug("🔎 Найдено ссылок на documents.html: {Count}", allDocLinks.Count);
            foreach (Match link in allDocLinks)
            {
                if (link.Groups.Count >= 2)
                {
                    string href = link.Groups[1].Value.Trim();
                    _logger.LogDebug("🔗 Ссылка: {Href}", href);
                }
            }
        }

        // ✅ РЕГУЛЯРКА #1: Универсальная — ищем href с documents.html + regNumber или noticeInfoId
        // Не зависит от текста внутри <a>, поддерживает /view/ и без него
        var regexUniversal = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?((?:https?://[^""'\s>]+|/[^""'\s>]*)documents\.html\?[^""'\s>]*(?:regNumber|noticeInfoId)\s*=\s*([0-9a-zA-Z\-]+)[^""'\s>]*)[""']?[^>]*?>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var match = regexUniversal.Match(html);
        if (match.Success && match.Groups.Count >= 3)
        {
            string fullUrl = match.Groups[1].Value.Trim();
            string identifier = match.Groups[2].Value.Trim();

            // Определяем тип идентификатора
            if (identifier.All(char.IsDigit))
            {
                result.NoticeInfoId = identifier;
                _logger.LogDebug("✅ Найдена ссылка с noticeInfoId={NoticeInfoId}", identifier);
            }
            else
            {
                result.RegNumber = identifier;
                _logger.LogDebug("✅ Найдена ссылка с regNumber={RegNumber}", identifier);
            }

            // Извлекаем lawType из контекста (рядом с ссылкой)
            int linkIndex = html.IndexOf(fullUrl, StringComparison.OrdinalIgnoreCase);
            if (linkIndex > 0)
            {
                int contextStart = Math.Max(0, linkIndex - 500);
                string context = html.Substring(contextStart, Math.Min(500, html.Length - contextStart));

                // Ищем lawType в контексте: 44, 223, 615, notice44, notice223, notice615, zk20, ea20
                var lawMatch = Regex.Match(context, @"(?:notice|law)?(44|223|615|zk|ea|z[0-9]+|e[0-9]+)(?:-фз|-пп|[^а-яё\-])?", RegexOptions.IgnoreCase);
                if (lawMatch.Success)
                {
                    result.LawType = "notice" + lawMatch.Groups[1].Value;
                    _logger.LogDebug("✅ Определён lawType={LawType}", result.LawType);
                }
            }

            // Нормализация URL: если относительный → добавляем базовый домен
            result.DocumentsPageUrl = fullUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? fullUrl
                : EisBaseUrl + (fullUrl.StartsWith("/") ? fullUrl : "/" + fullUrl);

            _logger.LogDebug("✅ DocumentsPageUrl={Url}", result.DocumentsPageUrl);
            return result;
        }

        // Проверка на капчу
        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("⚠️ ЕИС вернул страницу с капчей/блоком");
        }

        return result;
    }

    // 🔹 Этап 2.2: Получение списка документов
    public async Task<List<EisDocumentItem>> GetDocumentListAsync(
        string procurementUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Запрос списка документов для URL={Url}", procurementUrl);
        var documents = new List<EisDocumentItem>();

        try
        {
            var noticeInfo = await GetNoticeInfoIdAsync(procurementUrl, cancellationToken);
            if (noticeInfo == null || !noticeInfo.IsValid || string.IsNullOrEmpty(noticeInfo.DocumentsPageUrl))
            {
                _logger.LogWarning("Не удалось получить documentsPageUrl для URL={Url}", procurementUrl);
                return documents;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, noticeInfo.DocumentsPageUrl);
            // 🔹 Referer должен указывать на исходную страницу тендера
            request.Headers.Referrer = new Uri(procurementUrl);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ЕИС вернул статус {StatusCode} для documentsPageUrl={Url}", response.StatusCode, noticeInfo.DocumentsPageUrl);
                return documents;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            documents = ParseDocumentLinks(html);
            _logger.LogInformation("Найдено документов: {Count}", documents.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ошибка при получении списка документов для URL={Url}", procurementUrl);
        }
        return documents;
    }

    // 🔹 Парсинг ссылок на документы из HTML
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615 + прокси Tenderplan)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615 + все форматы)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615 + расширенный поиск)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    // 🔹 Парсинг ссылок на документы из HTML (УНИВЕРСАЛЬНЫЙ: 44/223/615)
    private List<EisDocumentItem> ParseDocumentLinks(string html)
    {
        var result = new List<EisDocumentItem>();

        // 🔍 ОТЛАДКА: логируем потенциальные ссылки на файлы
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var allFileLinks = Regex.Matches(html,
                @"<a[^>]*href\s*=\s*[""']?([^""'\s>]*(?:file\.html|download\.html|filestore|attachment)[^""'\s>]*)[""']?[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            _logger.LogDebug("🔎 Найдено потенциальных ссылок на файлы: {Count}", allFileLinks.Count);
        }

        // ✅ ВАРИАНТ 1: Стандартный паттерн ЕИС (44-ФЗ/615-ПП) — file.html?uid=HEX + title="filename"
        var regexStandard = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?((?:https?://)?zakupki\.gov\.ru/[^""'\s>]*/file\.html\?[^""'\s>]*uid\s*=\s*([0-9A-Fa-f\-]+)[^""'\s>]*)[""']?[^>]*?(?:title|data-filename|data-name)\s*=\s*[""']?([^""'>]+\.(?:pdf|docx?|xlsx?|zip|rar|7z|txt|rtf|odt))[""']?[^>]*?>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in regexStandard.Matches(html))
        {
            if (match.Groups.Count >= 4)
            {
                string downloadPath = match.Groups[1].Value.Trim();
                string fileId = match.Groups[2].Value.Trim();
                string fileName = match.Groups[3].Value.Trim();

                if (!string.IsNullOrEmpty(fileName) && !fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateDocumentItem(downloadPath, fileId, fileName));
                }
            }
        }

        // ✅ ВАРИАНТ 2: Паттерн для 223-ФЗ — download.html?id=NUM + data-tooltip с именем файла
        var regex223Tooltip = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?(/223/purchase/public/download/download\.html\?id\s*=\s*([0-9]+)[^""'\s>]*)[""']?[^>]*?data-tooltip\s*=\s*['""]<span\s+class\s*=\s*['""]custom-tooltiptext['""]>([^<>\r\n]+\.(?:pdf|docx?|xlsx?|zip|rar|7z|txt|rtf|odt))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in regex223Tooltip.Matches(html))
        {
            if (match.Groups.Count >= 4)
            {
                string downloadPath = match.Groups[1].Value.Trim();
                string fileId = match.Groups[2].Value.Trim();
                string fileName = match.Groups[3].Value.Trim();

                if (!string.IsNullOrEmpty(fileName) && !fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateDocumentItem(downloadPath, fileId, fileName));
                }
            }
        }

        // ✅ ВАРИАНТ 3: Фоллбэк — имя файла в тексте ссылки
        var regex223Text = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?(/223/purchase/public/download/download\.html\?id\s*=\s*([0-9]+)[^""'\s>]*)[""']?[^>]*?>([^<]+(?:\.pdf|\.docx?|\.xlsx?|\.zip|\.rar|\.7z|\.txt|\.rtf|\.odt)[^<]*)\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in regex223Text.Matches(html))
        {
            if (match.Groups.Count >= 4)
            {
                string downloadPath = match.Groups[1].Value.Trim();
                string fileId = match.Groups[2].Value.Trim();
                string fileName = match.Groups[3].Value.Trim();

                // Очищаем имя от дублирования расширений
                if (fileName.Contains(".pdf", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    int pdfIndex = fileName.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase);
                    fileName = fileName.Substring(0, pdfIndex + 4).Trim();
                }

                if (!string.IsNullOrEmpty(fileName) && !fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(CreateDocumentItem(downloadPath, fileId, fileName));
                }
            }
        }

        _logger.LogDebug("Парсинг документов: найдено {Count} ссылок (форматы: 44-ФЗ/223-ФЗ/615-ПП)", result.Count);
        return result;
    }

    // 🔹 ЕДИНЫЙ хелпер для создания EisDocumentItem (должен быть ТОЛЬКО ОДИН в файле!)
    private EisDocumentItem CreateDocumentItem(string downloadPath, string fileId, string fileName)
    {
        string downloadUrl = downloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? downloadPath
            : EisBaseUrl + (downloadPath.StartsWith("/") ? downloadPath : "/" + downloadPath);

        return new EisDocumentItem
        {
            FileName = fileName,
            FileId = fileId,
            MimeType = GetMimeType(fileName),
            DownloadUrl = downloadUrl,
            IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
            Status = "pending"
        };
    }

    // 🔹 ЕДИНЫЙ хелпер для создания EisDocumentItem (убедись, что он только один в файле!)


    // 🔹 Вспомогательный метод: обработка прямой ссылки
    private void ProcessDirectLink(Match match, List<EisDocumentItem> result)
    {
        string rawUrl = match.Groups[1].Value.Trim();
        string fileId = match.Groups[2].Value.Trim();
        string fileName = match.Groups[3].Value.Trim();
        string description = match.Groups[4].Value.Trim();

        if (string.IsNullOrEmpty(fileName) || fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase)) return;

        // Нормализация URL: если относительный → добавляем базовый домен
        string downloadUrl = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? rawUrl
            : EisBaseUrl + (rawUrl.StartsWith("/") ? rawUrl : "/" + rawUrl);

        result.Add(new EisDocumentItem
        {
            FileName = fileName,
            FileId = fileId,
            MimeType = GetMimeType(fileName),
            DownloadUrl = downloadUrl,
            IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
            Status = "pending"
        });
    }

    // 🔹 Вспомогательный метод: обработка прокси-ссылки Tenderplan
    private void ProcessProxyLink(Match match, List<EisDocumentItem> result)
    {
        string proxyPath = match.Groups[1].Value.Trim();  // /fileviewer?link=...
        string encodedLink = match.Groups[2].Value.Trim(); // URL-encoded реальная ссылка
        string fileName = match.Groups[3].Value.Trim();
        string description = match.Groups[4].Value.Trim();

        if (string.IsNullOrEmpty(fileName) || fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            // 🔹 ДЕКОДИРУЕМ ссылку из параметра link
            string decodedLink = System.Web.HttpUtility.UrlDecode(encodedLink);

            // Если декодированная ссылка — на zakupki.gov.ru, используем её напрямую
            // (наш HttpClient с куками справится с прямым запросом к ЕИС)
            if (decodedLink.StartsWith("https://zakupki.gov.ru", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new EisDocumentItem
                {
                    FileName = fileName,
                    FileId = Guid.NewGuid().ToString("N"), // Для прокси-ссылок генерируем временный ID
                    MimeType = GetMimeType(fileName),
                    DownloadUrl = decodedLink, // Используем прямую ссылку на ЕИС
                    IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase),
                    Status = "pending"
                });
                _logger.LogDebug("Прокси-ссылка декодирована: {Proxy} → {Direct}", proxyPath, decodedLink);
            }
            else
            {
                // Если ссылка осталась прокси-формата — оставляем как есть (фоллбэк)
                result.Add(new EisDocumentItem
                {
                    FileName = fileName,
                    FileId = Guid.NewGuid().ToString("N"),
                    MimeType = GetMimeType(fileName),
                    DownloadUrl = EisBaseUrl + (proxyPath.StartsWith("/") ? proxyPath : "/" + proxyPath),
                    IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                    Status = "pending"
                });
                _logger.LogDebug("Прокси-ссылка оставлена без изменений: {Proxy}", proxyPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось декодировать прокси-ссылку: {Encoded}", encodedLink);
            // Фоллбэк: добавляем как есть
            result.Add(new EisDocumentItem
            {
                FileName = fileName,
                FileId = Guid.NewGuid().ToString("N"),
                MimeType = GetMimeType(fileName),
                DownloadUrl = EisBaseUrl + (proxyPath.StartsWith("/") ? proxyPath : "/" + proxyPath),
                IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                Status = "pending"
            });
        }
    }

    private static string? GetMimeType(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".zip" or ".rar" or ".7z" => "application/zip",
            ".txt" => "text/plain",
            ".rtf" => "application/rtf",
            ".odt" => "application/vnd.oasis.opendocument.text",
            _ => "application/octet-stream"
        };
    }

    // 🔹 Этап 4: Скачивание файлов (ИСПРАВЛЕНО: с сессией и заголовками)
    public async Task<List<EisDocumentItem>> DownloadFilesAsync(
        string regNumber,
        List<EisDocumentItem> documents,
        IProgress<EisDocumentItem>? progress = null,
        CancellationToken cancellationToken = default,string? procurementUrl = null)
    {
        _logger.LogInformation("Начало скачивания {Count} документов для regNumber={RegNumber}", documents.Count, regNumber);
        string tenderFolder = Path.Combine(_storageRoot, regNumber);
        string rawFolder = Path.Combine(tenderFolder, "raw");
        Directory.CreateDirectory(rawFolder);

        var results = new List<EisDocumentItem>();

        foreach (var doc in documents)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                doc.Status = "skipped";
                doc.ErrorMessage = "Отменено пользователем";
                results.Add(doc);
                break;
            }

            if (string.IsNullOrEmpty(doc.DownloadUrl) || string.IsNullOrEmpty(doc.FileName))
            {
                doc.Status = "skipped";
                doc.ErrorMessage = "Нет URL или имени файла";
                results.Add(doc);
                continue;
            }

            try
            {
                doc.Status = "downloading";
                doc.DownloadProgress = 0.0;
                progress?.Report(doc);

                string safeFileName = GetSafeFileName(doc.FileName);
                string localPath = Path.Combine(rawFolder, safeFileName);

                if (File.Exists(localPath))
                {
                    _logger.LogInformation("Файл уже существует: {FileName}", doc.FileName);
                    doc.LocalPath = localPath;
                    doc.Status = "downloaded";
                    doc.DownloadProgress = 1.0;
                    progress?.Report(doc);
                    results.Add(doc);
                    continue;
                }

                // 🔹 КЛЮЧЕВОЕ: используем _httpClient (с куки) и добавляем Referer
                await DownloadFileWithProgressAsync(doc.DownloadUrl, localPath, doc, procurementUrl, progress, cancellationToken);

                doc.LocalPath = localPath;
                doc.Status = "downloaded";
                doc.DownloadProgress = 1.0;
                progress?.Report(doc);
                _logger.LogInformation("Скачан файл: {FileName} -> {LocalPath}", doc.FileName, localPath);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Доступ запрещён (403) для файла: {FileName}. Возможно, требуется авторизация в ЕИС.", doc.FileName);
                doc.Status = "error";
                doc.ErrorMessage = "ЕИС заблокировал запрос (403). Попробуйте авторизоваться на zakupki.gov.ru в браузере и повторить.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Ошибка при скачивании файла: {FileName}", doc.FileName);
                doc.Status = "error";
                doc.ErrorMessage = ex.Message;
            }
            results.Add(doc);
        }
        return results;
    }

    // 🔹 Скачивание файла с прогрессом (ИСПРАВЛЕНО: с сессией и заголовками)
    // 🔹 Скачивание файла с прогрессом и повторами при ошибках
    private async Task DownloadFileWithProgressAsync(
        string url,
        string destinationPath,
        EisDocumentItem doc,
        string? procurementUrl,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                if (!string.IsNullOrEmpty(procurementUrl))
                {
                    request.Headers.Referrer = new Uri(procurementUrl);
                }
                else
                {
                    request.Headers.Referrer = new Uri(EisBaseUrl);
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    totalRead += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        doc.DownloadProgress = (double)totalRead / totalBytes.Value;
                        progress?.Report(doc);
                    }
                }
                await fileStream.FlushAsync(cancellationToken);

                // ✅ Успех — выходим из цикла повторов
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
            {
                // 403 — часто временная блокировка, пробуем снова
                lastException = ex;
                _logger.LogDebug("Попытка {Attempt}/{Max} не удалась (403): {Url}",
                    attempt + 1, MaxRetryCount + 1, url);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Пользователь отменил — не повторяем
                throw;
            }
            catch (Exception ex)
            {
                // Другие ошибки сети — пробуем снова
                lastException = ex;
                _logger.LogDebug("Попытка {Attempt}/{Max} не удалась ({Type}): {Url}",
                    attempt + 1, MaxRetryCount + 1, ex.GetType().Name, url);
            }

            // ⏱ Ждём перед следующей попыткой (экспоненциальная задержка)
            if (attempt < MaxRetryCount && !cancellationToken.IsCancellationRequested)
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                _logger.LogDebug("Ожидание {Delay} перед повторной попыткой...", delay);
                await Task.Delay(delay, cancellationToken);
            }
        }

        // ❌ Все попытки исчерпаны — пробрасываем последнюю ошибку
        if (lastException != null)
        {
            throw lastException;
        }
    }

    private static string GetSafeFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }
        return fileName;
    }

    // 🔹 Этап 5: Извлечение текста (без изменений, работает корректно)
    public async Task<List<EisDocumentItem>> ExtractTextFromDocumentsAsync(
        string regNumber,
        List<EisDocumentItem> documents,
        IProgress<EisDocumentItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Начало извлечения текста из {Count} документов для regNumber={RegNumber}", documents.Count, regNumber);
        string tenderFolder = Path.Combine(_storageRoot, regNumber);
        string extractedFolder = Path.Combine(tenderFolder, "extracted");
        Directory.CreateDirectory(extractedFolder);

        var results = new List<EisDocumentItem>();
        var allTexts = new List<string>();

        foreach (var doc in documents)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                doc.TextExtractionStatus = "skipped";
                doc.TextExtractionError = "Отменено пользователем";
                results.Add(doc);
                break;
            }

            if (doc.Status != "downloaded" || string.IsNullOrEmpty(doc.LocalPath))
            {
                doc.TextExtractionStatus = "skipped";
                doc.TextExtractionError = "Файл не скачан";
                results.Add(doc);
                continue;
            }

            try
            {
                doc.TextExtractionStatus = "extracting";
                doc.TextExtractionProgress = 0.0;
                progress?.Report(doc);

                string? extractedText = await ExtractTextFromFileAsync(doc.LocalPath, doc, progress, cancellationToken);
                if (!string.IsNullOrEmpty(extractedText))
                {
                    string textFileName = Path.GetFileNameWithoutExtension(doc.FileName) + ".txt";
                    string textFilePath = Path.Combine(extractedFolder, textFileName);
                    File.WriteAllText(textFilePath, extractedText, Encoding.UTF8);

                    doc.ExtractedText = extractedText;
                    doc.ExtractedTextPath = textFilePath;
                    doc.TextExtractionStatus = "extracted";
                    doc.TextExtractionProgress = 1.0;
                    allTexts.Add($"=== {doc.FileName} ===\n{extractedText}");
                    _logger.LogInformation("Извлечён текст из {FileName}: {Chars} символов", doc.FileName, extractedText.Length);
                }
                else
                {
                    doc.TextExtractionStatus = "error";
                    doc.TextExtractionError = "Текст не извлечён (пустой или неподдерживаемый формат)";
                }
                progress?.Report(doc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Ошибка при извлечении текста из {FileName}", doc.FileName);
                doc.TextExtractionStatus = "error";
                doc.TextExtractionError = ex.Message;
            }
            results.Add(doc);
        }

        if (allTexts.Count > 0)
        {
            string combinedTextPath = Path.Combine(extractedFolder, "ALL_DOCUMENTS_COMBINED.txt");
            string combinedText = string.Join("\n---\n", allTexts);
            File.WriteAllText(combinedTextPath, combinedText, Encoding.UTF8);
            _logger.LogInformation("Сохранён объединённый текст: {Path}", combinedTextPath);
        }
        return results;
    }

    private async Task<string?> ExtractTextFromFileAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".txt" => await Task.FromResult(File.ReadAllText(filePath, Encoding.UTF8)),
            ".docx" => await ExtractTextFromDocxAsync(filePath, doc, progress, cancellationToken),
            ".odt" => await ExtractTextFromOdtAsync(filePath, doc, progress, cancellationToken),
            ".pdf" => await ExtractTextFromPdfAsync(filePath, doc, progress, cancellationToken),
            ".xlsx" => await ExtractTextFromExcelAsync(filePath, doc, progress, cancellationToken),
            ".zip" or ".rar" or ".7z" => await ExtractTextFromArchiveAsync(filePath, doc, progress, cancellationToken),
            _ => await Task.FromResult<string?>(null)
        };
    }

    // 🔹 Вспомогательные методы извлечения текста (без изменений)
    private async Task<string?> ExtractTextFromDocxAsync(string filePath, EisDocumentItem doc, IProgress<EisDocumentItem>? progress, CancellationToken cancellationToken) =>
        await Task.Run(() => { try { using var wordDoc = WordprocessingDocument.Open(filePath, false); var text = wordDoc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty; doc.TextExtractionProgress = 1.0; return CleanExtractedText(text); } catch { return null; } }, cancellationToken);

    private async Task<string?> ExtractTextFromOdtAsync(string filePath, EisDocumentItem doc, IProgress<EisDocumentItem>? progress, CancellationToken cancellationToken) =>
        await Task.Run(() => { try { using var archive = ZipFile.OpenRead(filePath); var entry = archive.GetEntry("content.xml"); if (entry == null) return null; using var stream = entry.Open(); var xml = new XmlDocument(); xml.Load(stream); doc.TextExtractionProgress = 1.0; return CleanExtractedText(xml.InnerText); } catch { return null; } }, cancellationToken);

    private async Task<string?> ExtractTextFromPdfAsync(string filePath, EisDocumentItem doc, IProgress<EisDocumentItem>? progress, CancellationToken cancellationToken) =>
        await Task.Run(() => { try { using var pdf = PdfDocument.Open(filePath); var sb = new StringBuilder(); foreach (var page in pdf.GetPages()) { var words = page.GetWords(); sb.AppendLine(string.Join(" ", words.Select(w => w.Text))); } doc.TextExtractionProgress = 1.0; return CleanExtractedText(sb.ToString()); } catch { return null; } }, cancellationToken);

    private async Task<string?> ExtractTextFromExcelAsync(string filePath, EisDocumentItem doc, IProgress<EisDocumentItem>? progress, CancellationToken cancellationToken) =>
        await Task.Run(() => { try { using var spreadsheet = SpreadsheetDocument.Open(filePath, false); var wbPart = spreadsheet.WorkbookPart; if (wbPart == null) return null; var wsPart = wbPart.WorksheetParts.FirstOrDefault(); if (wsPart == null) return null; var sheetData = wsPart.Worksheet?.Descendants<SheetData>().FirstOrDefault(); if (sheetData == null) return null; var sb = new StringBuilder(); foreach (var row in sheetData.Elements<Row>()) { var rowText = new StringBuilder(); foreach (var cell in row.Elements<Cell>()) { var val = GetCellValue(cell, spreadsheet); if (!string.IsNullOrEmpty(val)) rowText.Append(val).Append(" | "); } if (rowText.Length > 0) sb.AppendLine(rowText.ToString()); } doc.TextExtractionProgress = 1.0; return CleanExtractedText(sb.ToString()); } catch { return null; } }, cancellationToken);

    private static string? GetCellValue(Cell cell, SpreadsheetDocument spreadsheetDocument)
    {
        if (cell.CellValue == null) return null;
        string value = cell.CellValue.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            var sst = spreadsheetDocument.WorkbookPart?.SharedStringTablePart?.SharedStringTable;
            if (sst != null && int.TryParse(value, out int id))
            {
                var items = sst.Elements<SharedStringItem>();
                if (id >= 0 && id < items.Count())
                {
                    var item = items.ElementAtOrDefault(id);
                    if (item != null) return item.InnerText;
                }
            }
        }
        return value;
    }

    private async Task<string?> ExtractTextFromArchiveAsync(string filePath, EisDocumentItem doc, IProgress<EisDocumentItem>? progress, CancellationToken cancellationToken) =>
        await Task.Run(async () => { try { var extractDir = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, Path.GetFileNameWithoutExtension(filePath)); if (!Directory.Exists(extractDir)) ZipFile.ExtractToDirectory(filePath, extractDir); var sb = new StringBuilder(); var files = Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories); for (int i = 0; i < files.Length; i++) { var f = files[i]; var ext = Path.GetExtension(f).ToLowerInvariant(); if (ext is ".txt" or ".docx" or ".odt" or ".pdf" or ".xlsx") { var text = await ExtractTextFromFileAsync(f, doc, progress, cancellationToken); if (!string.IsNullOrEmpty(text)) { sb.AppendLine($"=== {Path.GetFileName(f)} ==="); sb.AppendLine(text); } } doc.TextExtractionProgress = (double)(i + 1) / files.Length; progress?.Report(doc); } return CleanExtractedText(sb.ToString()); } catch { return null; } }, cancellationToken);

    private static string CleanExtractedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"\r\n\s*\r\n", "\n\n");
        text = Regex.Replace(text, @"[ \t]+", " ");
        return text.Trim();
    }

    // 🔹 Повторная загрузка только файлов со статусом "error"
    // 🔹 Повторная загрузка только файлов со статусом "error"
    public async Task<List<EisDocumentItem>> RetryFailedDownloadsAsync(
        string regNumber,
        List<EisDocumentItem> documents,
        IProgress<EisDocumentItem>? progress = null,
        CancellationToken cancellationToken = default,
        string? procurementUrl = null)  // ← НОВЫЙ параметр
    {
        var failedDocs = documents
            .Where(d => d.Status == "error" && d.CanRetry)
            .ToList();

        if (failedDocs.Count == 0)
        {
            _logger.LogInformation("Нет файлов для повторной загрузки (regNumber={RegNumber})", regNumber);
            return documents;
        }

        _logger.LogInformation("Повторная загрузка {Count} файлов для regNumber={RegNumber}",
            failedDocs.Count, regNumber);

        // Обновляем метаданные перед повтором
        foreach (var doc in failedDocs)
        {
            doc.RetryCount++;
            doc.LastRetryAt = DateTime.UtcNow;
            doc.Status = "pending";
            doc.ErrorMessage = null;
            doc.DownloadProgress = 0.0;
        }

        // Вызываем основной метод загрузки только для неудачных файлов
        // 🔹 КЛЮЧЕВОЕ: передаём procurementUrl
        var results = await DownloadFilesAsync(regNumber, failedDocs, progress, cancellationToken, procurementUrl);

        // Обновляем исходный список результатами
        foreach (var result in results)
        {
            var original = documents.FirstOrDefault(d => d.FileId == result.FileId);
            if (original != null)
            {
                original.Status = result.Status;
                original.LocalPath = result.LocalPath;
                original.ErrorMessage = result.ErrorMessage;
                original.DownloadProgress = result.DownloadProgress;
                original.RetryCount = result.RetryCount;
                original.LastRetryAt = result.LastRetryAt;
            }
        }

        return documents;
    }

    // 🔹 IDisposable для корректного освобождения ресурсов
    public void Dispose()
    {
        _httpClient?.Dispose();
        _handler?.Dispose();
    }
}