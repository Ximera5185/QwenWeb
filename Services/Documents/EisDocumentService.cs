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

        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
    }

    // 🔹 Этап 2.1: Извлечение noticeInfoId
    public async Task<NoticeInfoResult?> GetNoticeInfoIdAsync(
        string procurementUrl,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Поиск noticeInfoId для URL={Url}", procurementUrl);

        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, procurementUrl);
            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ЕИС вернул статус {StatusCode} для URL={Url}", response.StatusCode, procurementUrl);
                return null;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("Получено HTML, длина: {Length} символов", html.Length);

            NoticeInfoResult result = ParseNoticeInfo(html);

            if (result.IsValid)
            {
                _logger.LogInformation("Найдено: noticeInfoId={NoticeInfoId}, lawType={LawType}",
                    result.NoticeInfoId, result.LawType);
            }
            else
            {
                _logger.LogWarning("Не удалось извлечь noticeInfoId из URL={Url}", procurementUrl);
            }

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Ошибка HTTP при поиске noticeInfoId для URL={Url}", procurementUrl);
            return null;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogInformation(ex, "Запрос отменён для URL={Url}", procurementUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Неожиданная ошибка при поиске noticeInfoId для URL={Url}", procurementUrl);
            return null;
        }
    }

    private static NoticeInfoResult ParseNoticeInfo(string html)
    {
        NoticeInfoResult result = new NoticeInfoResult();

        Regex documentsLinkRegex = new Regex(
            @"<a[^>]+href=[""']?(/epz/order/notice/(notice\d+)/documents\.html\?noticeInfoId=(\d+))[""']?[^>]*>\s*Документы\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Match match = documentsLinkRegex.Match(html);

        if (match.Success && match.Groups.Count >= 4)
        {
            result.DocumentsPageUrl = EisBaseUrl + match.Groups[1].Value.Trim();
            result.LawType = match.Groups[2].Value.Trim();
            result.NoticeInfoId = match.Groups[3].Value.Trim();
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
                _logger.LogWarning("Не удалось получить documentsPageUrl для URL={Url}", procurementUrl);
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

            _logger.LogInformation("Найдено документов: {Count} для noticeInfoId={NoticeInfoId}",
                documents.Count, noticeInfo.NoticeInfoId);
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

    private static List<EisDocumentItem> ParseDocumentLinks(string html)
    {
        List<EisDocumentItem> result = new List<EisDocumentItem>();

        Regex linkRegex = new Regex(
            @"<a[^>]*href=[""']?([^""'\s>]*download\.html\?[^""'\s>]*id=(\d+)[^""'\s>]*)[""']?[^>]*data-tooltip=['""]?<span[^>]*class=['""][^'""]*custom-tooltiptext[^'""]*['""][^>]*>([^<]+)</span>['""]?[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        MatchCollection matches = linkRegex.Matches(html);

        foreach (Match match in matches)
        {
            if (match.Groups.Count < 4)
            {
                continue;
            }

            string downloadPath = match.Groups[1].Value.Trim();
            string docId = match.Groups[2].Value.Trim();
            string fileNameRaw = match.Groups[3].Value.Trim();

            string fileName = HttpUtility.HtmlDecode(fileNameRaw).Trim();

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
                FileId = docId,
                MimeType = mimeType,
                DownloadUrl = EisBaseUrl + downloadPath,
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
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

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
                // ODT — это ZIP-архив. Открываем его напрямую.
                using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(filePath);

                // Текст всегда лежит в content.xml
                System.IO.Compression.ZipArchiveEntry? entry = archive.GetEntry("content.xml");
                if (entry == null) return null;

                using Stream stream = entry.Open();

                // Загружаем XML
                System.Xml.XmlDocument xmlDocument = new System.Xml.XmlDocument();
                xmlDocument.Load(stream);

                // Извлекаем весь текстовый контент
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

                    // ✅ Исправлено для PdfPig 1.7.0-custom-5:
                    // Собираем текст из слов, так как page.Text не доступен
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
        return await Task.Run(() =>
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
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".docx" or ".odt" or ".pdf" or ".xlsx")
                    {
                        string? fileText = ExtractTextFromFileAsync(file, doc, progress, cancellationToken).Result;
                        if (!string.IsNullOrEmpty(fileText))
                        {
                            textBuilder.AppendLine($"\n=== {Path.GetFileName(file)} ===");
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