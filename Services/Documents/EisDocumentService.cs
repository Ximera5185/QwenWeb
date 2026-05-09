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
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PdfPage = UglyToad.PdfPig.Content.Page;

namespace QwenWeb.Services.Documents;

public class EisDocumentService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EisDocumentService> _logger;
    private readonly string _storageRoot;
    private const string EisBaseUrl = "https://zakupki.gov.ru";

    public EisDocumentService(HttpClient httpClient, ILogger<EisDocumentService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _storageRoot = Path.Combine(AppContext.BaseDirectory, "Storage", "Tenders");

        // ✅ Заголовки для обхода защиты ЕИС (403/капча)
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
    public async Task<NoticeInfoResult?> GetNoticeInfoIdAsync(
        string procurementUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🔍 Поиск идентификатора закупки для URL={Url}", procurementUrl);

        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, procurementUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ ЕИС вернул статус {StatusCode} для URL={Url}", response.StatusCode, procurementUrl);
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("📄 Получено HTML, длина: {Length} символов", html.Length);

            // 🔍 Отладка: логируем ссылки "Документы" для анализа
            if (_logger.IsEnabled(LogLevel.Debug) && !string.IsNullOrEmpty(html))
            {
                var docLinks = Regex.Matches(html,
                    @"<a[^>]*href=[^>]*>[\s\n\r]*Документы[\s\n\r]*</a>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match link in docLinks)
                {
                    _logger.LogDebug("🔗 Найдена ссылка 'Документы': {Link}", link.Value);
                }

                if (!html.Contains("noticeInfoId", StringComparison.OrdinalIgnoreCase) &&
                    !html.Contains("regNumber", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("⚠️ Не найдены ни noticeInfoId, ни regNumber в ответе ЕИС");
                }
            }

            NoticeInfoResult result = ParseNoticeInfo(html);

            if (result.IsValid)
            {
                string identifier = !string.IsNullOrEmpty(result.NoticeInfoId)
                    ? $"noticeInfoId={result.NoticeInfoId}"
                    : $"regNumber={result.RegNumber}";
                _logger.LogInformation("✅ Найдено: {Identifier}, lawType={LawType}, url={Url}",
                    identifier, result.LawType, result.DocumentsPageUrl);
            }
            else
            {
                _logger.LogWarning("❌ Не удалось извлечь идентификатор из URL={Url}. HTML содержит 'Документы': {HasLink}",
                    procurementUrl, html.Contains("Документы", StringComparison.OrdinalIgnoreCase));
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "⚠️ Ошибка HTTP при поиске идентификатора для URL={Url}", procurementUrl);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogInformation(ex, "⏹ Запрос отменён для URL={Url}", procurementUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 Неожиданная ошибка при поиске идентификатора для URL={Url}", procurementUrl);
            return null;
        }
    }

    // 🔹 Парсинг noticeInfoId или regNumber из HTML (НЕ static — нужен _logger)
    // 🔹 УБРАТЬ 'static' — чтобы метод мог использовать _logger
    // 🔹 Парсинг noticeInfoId или regNumber из HTML
    private NoticeInfoResult ParseNoticeInfo(string html)
    {
        NoticeInfoResult result = new NoticeInfoResult();

        // 🔍 Быстрая валидация
        if (string.IsNullOrWhiteSpace(html) || html.Length < 1000)
        {
            _logger.LogDebug("HTML слишком короткий для парсинга: {Length} символов", html.Length);
            return result;
        }

        // ✅ ШАГ 1: Сначала пробуем распарсить (не блокируем из-за возможных ложных срабатываний)

        // РЕГУЛЯРКА #1: Новая структура — ссылка с regNumber (приоритетная)
        Regex regexRegNumber = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?(/epz/order/notice/([^/]+)/documents\.html\?[^""'\s>]*regNumber\s*=\s*([0-9a-zA-Z]+)[^""'\s>]*)[""']?[^>]*?>\s*Документы\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Match match = regexRegNumber.Match(html);
        if (match.Success && match.Groups.Count >= 4)
        {
            result.DocumentsPageUrl = EisBaseUrl + match.Groups[1].Value.Trim();
            result.LawType = match.Groups[2].Value.Trim();
            result.NoticeInfoId = null;
            result.RegNumber = match.Groups[3].Value.Trim();
            _logger.LogDebug("✅ Найдена ссылка с regNumber={RegNumber}, lawType={LawType}",
                result.RegNumber, result.LawType);
            return result; // ← Возвращаем сразу, если нашли
        }

        // РЕГУЛЯРКА #2: Старая структура — с noticeInfoId (фоллбэк)
        Regex regexNoticeId = new Regex(
            @"<a\s+[^>]*?href\s*=\s*[""']?(/epz/order/notice/([^/]+)/documents\.html\?[^""'\s>]*noticeInfoId\s*=\s*(\d+)[^""'\s>]*)[""']?[^>]*?>\s*Документы\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        match = regexNoticeId.Match(html);
        if (match.Success && match.Groups.Count >= 4)
        {
            result.DocumentsPageUrl = EisBaseUrl + match.Groups[1].Value.Trim();
            result.LawType = match.Groups[2].Value.Trim();
            result.NoticeInfoId = match.Groups[3].Value.Trim();
            result.RegNumber = null;
            _logger.LogDebug("✅ Найдена ссылка с noticeInfoId={NoticeInfoId}, lawType={LawType}",
                result.NoticeInfoId, result.LawType);
            return result;
        }

        // ❌ ШАГ 2: Если парсинг не удался — проверяем, не капча ли это
        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            (html.Contains("bot", StringComparison.OrdinalIgnoreCase) && html.Contains("защита", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("⚠️ ЕИС вернул страницу с капчей/блоком (парсинг не удался)");
            return result;
        }

        // ❌ ШАГ 3: Логируем контекст для отладки, если не нашли ни ссылку, ни капчу
        _logger.LogWarning("❌ Не удалось извлечь ссылку на документы. HTML содержит 'Документы': {HasLink}",
            html.Contains("Документы", StringComparison.OrdinalIgnoreCase));

        int docIndex = html.IndexOf("Документы", StringComparison.OrdinalIgnoreCase);
        if (docIndex >= 0)
        {
            int start = Math.Max(0, docIndex - 500);
            int end = Math.Min(html.Length, docIndex + 500);
            string context = html.Substring(start, end - start).Replace("\r\n", " ").Replace("\n", " ");
            _logger.LogDebug("🔎 Контекст вокруг 'Документы': {Preview}", context);
        }

        return result;
    }

    // 🔹 Этап 2.2: Получение списка документов
    public async Task<List<EisDocumentItem>> GetDocumentListAsync(
        string procurementUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Запрос списка документов для URL={Url}", procurementUrl);
        List<EisDocumentItem> documents = new List<EisDocumentItem>();

        try
        {
            NoticeInfoResult? noticeInfo = await GetNoticeInfoIdAsync(procurementUrl, cancellationToken);

            if (noticeInfo == null || !noticeInfo.IsValid || string.IsNullOrEmpty(noticeInfo.DocumentsPageUrl))
            {
                _logger.LogWarning("Не удалось получить documentsPageUrl для URL={Url}. IsValid={IsValid}, NoticeInfoId={NoticeInfoId}, RegNumber={RegNumber}",
                    procurementUrl, noticeInfo?.IsValid, noticeInfo?.NoticeInfoId, noticeInfo?.RegNumber);
                return documents;
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, noticeInfo.DocumentsPageUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ЕИС вернул статус {StatusCode} для documentsPageUrl={Url}",
                    response.StatusCode, noticeInfo.DocumentsPageUrl);
                return documents;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Получено HTML страницы документов, длина: {Length} символов", html.Length);

            documents = ParseDocumentLinks(html);

            string identifier = !string.IsNullOrEmpty(noticeInfo.NoticeInfoId)
                ? $"noticeInfoId={noticeInfo.NoticeInfoId}"
                : $"regNumber={noticeInfo.RegNumber}";

            _logger.LogInformation("Найдено документов: {Count} для {Identifier}",
                documents.Count, identifier);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Ошибка HTTP при запросе списка документов для URL={Url}", procurementUrl);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogInformation(ex, "Запрос отменён для URL={Url}", procurementUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неожиданная ошибка при получении списка документов для URL={Url}", procurementUrl);
        }

        return documents;
    }

    // 🔹 Парсинг ссылок на документы из HTML (НЕ static — нужен _logger)
    private List<EisDocumentItem> ParseDocumentLinks(string html)
    {
        List<EisDocumentItem> result = new List<EisDocumentItem>();

        // ✅ НОВАЯ РЕГУЛЯРКА: ищем <span class="section__value"><a href="...file.html?uid=XXX" title="FileName.ext">Description</a></span>
        // Поддерживает как абсолютные (https://...), так и относительные (/epz/...) пути
        Regex linkRegex = new Regex(
            @"<span\s+class\s*=\s*[""']section__value[""']\s*>[^<]*<a\s+[^>]*href\s*=\s*[""']?((?:https?://[^""'\s>]+|/[^""'\s>]*)file\.html\?uid\s*=\s*([0-9A-Fa-f]+)[^""'\s>]*)[""']?[^>]*title\s*=\s*[""']([^""']+\.(?:pdf|docx?|xlsx?|zip|rar|7z|txt|rtf|odt))[""'][^>]*>([^<]+)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        MatchCollection matches = linkRegex.Matches(html);
        _logger.LogDebug("Парсинг документов: найдено {Count} совпадений", matches.Count);

        foreach (Match match in matches)
        {
            if (match.Groups.Count < 5) continue;

            string downloadPath = match.Groups[1].Value.Trim();
            string uid = match.Groups[2].Value.Trim();
            string fileName = match.Groups[3].Value.Trim();
            string description = match.Groups[4].Value.Trim();

            // Пропускаем подписи и короткие имена
            if (string.IsNullOrEmpty(fileName) ||
                fileName.Contains("ЭЦП", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains("подпись", StringComparison.OrdinalIgnoreCase) ||
                fileName.Trim().Length < 2)
            {
                continue;
            }

            string? mimeType = GetMimeType(fileName);

            EisDocumentItem item = new EisDocumentItem
            {
                FileName = fileName,
                FileId = uid,
                MimeType = mimeType,
                DownloadUrl = downloadPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? downloadPath
                    : EisBaseUrl + downloadPath,
                IsArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
                Status = "pending"
            };

            result.Add(item);
        }

        return result;
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

    // 🔹 Этап 4: Скачивание файлов
    public async Task<List<EisDocumentItem>> DownloadFilesAsync(
        string regNumber,
        List<EisDocumentItem> documents,
        IProgress<EisDocumentItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Начало скачивания {Count} документов для regNumber={RegNumber}",
            documents.Count, regNumber);

        string tenderFolder = Path.Combine(_storageRoot, regNumber);
        string rawFolder = Path.Combine(tenderFolder, "raw");

        if (!Directory.Exists(rawFolder))
        {
            Directory.CreateDirectory(rawFolder);
        }

        List<EisDocumentItem> results = new List<EisDocumentItem>();

        foreach (EisDocumentItem doc in documents)
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

                await DownloadFileWithProgressAsync(doc.DownloadUrl, localPath, doc, progress, cancellationToken);

                doc.LocalPath = localPath;
                doc.Status = "downloaded";
                doc.DownloadProgress = 1.0;
                progress?.Report(doc);

                _logger.LogInformation("Скачан файл: {FileName} -> {LocalPath}", doc.FileName, localPath);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("Доступ запрещён (403) для файла: {FileName}", doc.FileName);
                doc.Status = "error";
                doc.ErrorMessage = "ЕИС заблокировал запрос (403)";
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Скачивание отменено для файла: {FileName}", doc.FileName);
                doc.Status = "skipped";
                doc.ErrorMessage = "Отменено";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при скачивании файла: {FileName}", doc.FileName);
                doc.Status = "error";
                doc.ErrorMessage = ex.Message;
            }

            results.Add(doc);
        }

        _logger.LogInformation("Завершено скачивание. Успешно: {Success}, Ошибки: {Errors}",
            results.Count(r => r.Status == "downloaded"),
            results.Count(r => r.Status == "error"));

        return results;
    }

    private async Task DownloadFileWithProgressAsync(
        string url,
        string destinationPath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using FileStream fileStream = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

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
    }

    private static string GetSafeFileName(string fileName)
    {
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar, '_');
        }
        return fileName;
    }

    // 🔹 Этап 5: Извлечение текста
    public async Task<List<EisDocumentItem>> ExtractTextFromDocumentsAsync(
        string regNumber,
        List<EisDocumentItem> documents,
        IProgress<EisDocumentItem>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Начало извлечения текста из {Count} документов для regNumber={RegNumber}",
            documents.Count, regNumber);

        string tenderFolder = Path.Combine(_storageRoot, regNumber);
        string extractedFolder = Path.Combine(tenderFolder, "extracted");

        if (!Directory.Exists(extractedFolder))
        {
            Directory.CreateDirectory(extractedFolder);
        }

        List<EisDocumentItem> results = new List<EisDocumentItem>();
        List<string> allTexts = new List<string>();

        foreach (EisDocumentItem doc in documents)
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

                    _logger.LogInformation("Извлечён текст из {FileName}: {Chars} символов",
                        doc.FileName, extractedText.Length);
                }
                else
                {
                    doc.TextExtractionStatus = "error";
                    doc.TextExtractionError = "Текст не извлечён (пустой или неподдерживаемый формат)";
                }

                progress?.Report(doc);
            }
            catch (Exception ex)
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
            string combinedText = string.Join("\n\n---\n\n", allTexts);
            File.WriteAllText(combinedTextPath, combinedText, Encoding.UTF8);

            _logger.LogInformation("Сохранён объединённый текст: {Path}", combinedTextPath);
        }

        _logger.LogInformation("Завершено извлечение текста. Успешно: {Success}, Ошибки: {Errors}",
            results.Count(r => r.TextExtractionStatus == "extracted"),
            results.Count(r => r.TextExtractionStatus == "error"));

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
            ".xls" => await Task.FromResult<string?>("[.xls формат требует дополнительной библиотеки]"),
            ".zip" or ".rar" or ".7z" => await ExtractTextFromArchiveAsync(filePath, doc, progress, cancellationToken),
            _ => await Task.FromResult<string?>(null)
        };
    }

    private async Task<string?> ExtractTextFromDocxAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using WordprocessingDocument wordDocument = WordprocessingDocument.Open(filePath, false);

                Body? body = wordDocument.MainDocumentPart?.Document?.Body;
                if (body == null) return null;

                string text = body.InnerText ?? string.Empty;
                doc.TextExtractionProgress = 1.0;
                return CleanExtractedText(text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при чтении DOCX: {FilePath}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    private async Task<string?> ExtractTextFromOdtAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                ZipArchiveEntry? entry = archive.GetEntry("content.xml");
                if (entry == null) return null;

                using Stream stream = entry.Open();
                XmlDocument xmlDocument = new XmlDocument();
                xmlDocument.Load(stream);

                string rawText = xmlDocument.InnerText;
                doc.TextExtractionProgress = 1.0;
                return CleanExtractedText(rawText);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при чтении ODT: {FilePath}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    private async Task<string?> ExtractTextFromPdfAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using PdfDocument document = PdfDocument.Open(filePath);
                StringBuilder textBuilder = new StringBuilder();

                int totalPages = document.NumberOfPages;
                for (int pageNum = 1; pageNum <= totalPages; pageNum++)
                {
                    PdfPage page = document.GetPage(pageNum);
                    IEnumerable<Word> words = page.GetWords();
                    string pageText = string.Join(" ", words.Select(w => w.Text));

                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        textBuilder.AppendLine(pageText);
                    }

                    doc.TextExtractionProgress = (double)pageNum / totalPages;
                    progress?.Report(doc);
                }

                return CleanExtractedText(textBuilder.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при чтении PDF: {FilePath}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    private async Task<string?> ExtractTextFromExcelAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                StringBuilder textBuilder = new StringBuilder();

                using SpreadsheetDocument spreadsheetDocument = SpreadsheetDocument.Open(filePath, false);

                WorkbookPart? workbookPart = spreadsheetDocument.WorkbookPart;
                if (workbookPart == null) return null;

                WorksheetPart? worksheetPart = workbookPart.WorksheetParts.FirstOrDefault();
                if (worksheetPart == null) return null;

                SheetData? sheetData = worksheetPart.Worksheet?.Descendants<SheetData>().FirstOrDefault();
                if (sheetData == null) return null;

                foreach (Row row in sheetData.Elements<Row>())
                {
                    StringBuilder rowText = new StringBuilder();
                    foreach (Cell cell in row.Elements<Cell>())
                    {
                        string? cellValue = GetCellValue(cell, spreadsheetDocument);
                        if (!string.IsNullOrEmpty(cellValue))
                        {
                            rowText.Append(cellValue).Append(" | ");
                        }
                    }
                    if (rowText.Length > 0)
                    {
                        textBuilder.AppendLine(rowText.ToString());
                    }
                }

                doc.TextExtractionProgress = 1.0;
                return CleanExtractedText(textBuilder.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при чтении Excel: {FilePath}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    private static string? GetCellValue(Cell cell, SpreadsheetDocument spreadsheetDocument)
    {
        if (cell.CellValue == null) return null;

        string value = cell.CellValue.InnerText ?? string.Empty;

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            SharedStringTable? sharedStringTable = spreadsheetDocument.WorkbookPart?.SharedStringTablePart?.SharedStringTable;

            if (sharedStringTable != null && int.TryParse(value, out int id))
            {
                IEnumerable<SharedStringItem> items = sharedStringTable.Elements<SharedStringItem>();
                if (id >= 0 && id < items.Count())
                {
                    SharedStringItem? item = items.ElementAtOrDefault(id);
                    if (item != null)
                    {
                        return item.InnerText;
                    }
                }
            }
        }

        return value;
    }

    private async Task<string?> ExtractTextFromArchiveAsync(
        string filePath,
        EisDocumentItem doc,
        IProgress<EisDocumentItem>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            try
            {
                string extractDir = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(filePath));

                if (!Directory.Exists(extractDir))
                {
                    ZipFile.ExtractToDirectory(filePath, extractDir);
                }

                StringBuilder textBuilder = new StringBuilder();
                string[] files = Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories);
                int processedFiles = 0;

                foreach (string file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".docx" or ".odt" or ".pdf" or ".xlsx")
                    {
                        string? fileText = await ExtractTextFromFileAsync(file, doc, progress, cancellationToken);
                        if (!string.IsNullOrEmpty(fileText))
                        {
                            textBuilder.AppendLine($"=== {Path.GetFileName(file)} ===");
                            textBuilder.AppendLine(fileText);
                        }
                    }

                    processedFiles++;
                    if (files.Length > 0)
                    {
                        doc.TextExtractionProgress = (double)processedFiles / files.Length;
                        progress?.Report(doc);
                    }
                }
                return CleanExtractedText(textBuilder.ToString());
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при распаковке архива: {FilePath}", filePath);
                return null;
            }
        }, cancellationToken);
    }

    private static string CleanExtractedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = Regex.Replace(text, @"\n\s*\n", "\n\n");
        text = Regex.Replace(text, @"[ \t]+", " ");
        return text.Trim();
    }
}