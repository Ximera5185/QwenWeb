// File: Services/Tenderplan/TenderplanApiProvider.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QwenWeb.Configuration;
using QwenWeb.Models;

namespace QwenWeb.Services.Tenderplan;

/// <summary>
/// Провайдер для получения тендеров через Tenderplan API v3.5.0.
/// Реализует ITenderSourceProvider. Полностью изолирован от RSS-модуля.
/// </summary>
public class TenderplanApiProvider : ITenderSourceProvider
{
    // 🔹 Private readonly fields
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TenderplanSettings _settings;
    private readonly ILogger<TenderplanApiProvider> _logger;

    // 🔹 Public properties
    public bool IsConfigured => !string.IsNullOrEmpty(GetAccessToken());

    // 🔹 Constructors
    public TenderplanApiProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<TenderplanSettings> settings,
        ILogger<TenderplanApiProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // 🔹 Public methods
    // 🔹 Public methods
    public async Task<IReadOnlyList<TenderplanRecord>> FetchAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Tenderplan API не настроен: отсутствует AccessToken в UserSecrets/Env.");
            return Array.Empty<TenderplanRecord>();
        }

        string? accessToken = GetAccessToken();
        using HttpClient client = _httpClientFactory.CreateClient("TenderplanApi");

        if (!string.IsNullOrEmpty(accessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        // 🔹 ТЕСТОВЫЙ ЗАПРОС: поиск по конкретному номеру тендера
        // Замените на реальный номер из ЛК или сайта
        string testTenderNumber = "32615973598"; // ← ВСТАВЬТЕ ВАШ НОМЕР ЗДЕСЬ

        // 🔹 Вариант А: Эндпоинт получения одного тендера по ID
        // string requestUri = $"/api/tenders/v2/fullinfo?id={testTenderNumber}";

        // 🔹 Вариант Б: Поиск по номеру через search/tender (более надёжно)
        string requestUri = $"/api/search/tender?number={Uri.EscapeDataString(testTenderNumber)}&page=1&pageSize=1";

        _logger.LogDebug("Запрос к Tenderplan API (поиск по номеру '{Number}'): {Uri}", testTenderNumber, requestUri);

        try
        {
            using HttpResponseMessage response = await client.GetAsync(requestUri, cancellationToken);

            // 🔍 Логирование ответа для отладки
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("❌ API вернул ошибку {StatusCode}: {Content}",
                    response.StatusCode,
                    errorContent.Length > 500 ? errorContent.Substring(0, 500) + "..." : errorContent);
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            // 🔍 ОТЛАДОЧНЫЙ ЛОГ — только для разработки
            _logger.LogDebug("🔍 RAW API Response: {Json}", json);

            var records = ParseApiResponse(json);
            _logger.LogInformation("Получено {Count} тендеров от Tenderplan API.", records.Count);

            // 🔍 Если нашли тендер — выводим его детали в лог
            if (records.Count > 0)
            {
                var first = records[0];
                _logger.LogInformation("✅ УСПЕХ! Найден тендер: ID={Id}, Title={Title}, Price={Price}",
                    first.TenderId, first.Title, first.InitialPrice);
            }

            return records;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "❌ Ошибка запроса к Tenderplan API. URI: {Uri}", requestUri);
            throw;
        }
    }

    // 🔹 Private helpers
    private string? GetAccessToken()
    {
        string? token = _configuration["Tenderplan:AccessToken"];
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private List<TenderplanRecord> ParseApiResponse(string json)
    {
        var records = new List<TenderplanRecord>();
        if (string.IsNullOrWhiteSpace(json)) return records;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // 🔹 Попытка 1: Прямой массив (наиболее вероятно)
            if (json.TrimStart().StartsWith("["))
            {
                var directArray = JsonSerializer.Deserialize<List<TenderplanApiDto>>(json, options);
                if (directArray != null)
                {
                    foreach (var dto in directArray)
                    {
                        if (string.IsNullOrEmpty(dto.Id) && string.IsNullOrEmpty(dto.Number)) continue;
                        records.Add(MapDtoToRecord(dto));
                    }
                    return records;
                }
            }

            // 🔹 Попытка 2: Объект с полем "tenders"
            var withTenders = JsonSerializer.Deserialize<TenderplanApiResponse>(json, options);
            if (withTenders?.Tenders != null && withTenders.Tenders.Count > 0)
            {
                foreach (var dto in withTenders.Tenders)
                {
                    if (string.IsNullOrEmpty(dto.Id) && string.IsNullOrEmpty(dto.Number)) continue;
                    records.Add(MapDtoToRecord(dto));
                }
                return records;
            }

            // 🔹 Попытка 3: Объект с полем "items" / "data" / "result" / "list"
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var propName in new[] { "items", "data", "result", "list", "records", "tenders" })
            {
                if (root.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    var array = JsonSerializer.Deserialize<List<TenderplanApiDto>>(prop.GetRawText(), options);
                    if (array != null)
                    {
                        foreach (var dto in array)
                        {
                            if (string.IsNullOrEmpty(dto.Id) && string.IsNullOrEmpty(dto.Number)) continue;
                            records.Add(MapDtoToRecord(dto));
                        }
                        return records;
                    }
                }
            }

            // 🔹 Если ничего не подошло — логируем структуру для отладки
            _logger.LogWarning("⚠️ Неизвестная структура ответа API. Корневые свойства: {Props}",
                string.Join(", ", root.EnumerateObject().Select(p => p.Name)));
            _logger.LogDebug("🔍 RAW JSON (first 1000 chars): {Json}",
                json.Length > 1000 ? json.Substring(0, 1000) + "..." : json);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "❌ Ошибка десериализации ответа Tenderplan API. JSON: {JsonPreview}",
                json.Length > 500 ? json.Substring(0, 500) + "..." : json);
        }

        return records;
    }

    // 🔹 Маппер DTO → Record (вынесен для повторного использования)
    private static TenderplanRecord MapDtoToRecord(TenderplanApiDto dto)
    {
        return new TenderplanRecord
        {
            TenderId = dto.Id ?? dto.TenderId ?? dto.Number ?? dto.RegNumber ?? string.Empty,
            Title = dto.Title ?? dto.Name ?? dto.ObjectName ?? string.Empty,
            RegNumber = dto.RegNumber ?? dto.Number ?? dto.ProcurementNumber,
            InitialPrice = dto.InitialPrice ?? dto.Price ?? dto.Nmc ?? dto.NmcSum,
            Currency = dto.Currency ?? dto.CurrencyCode ?? "RUB",
            PublishDateUtc = dto.PublishDate ?? dto.PublicationDate ?? dto.CreatedAt,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    // 🔹 Private nested DTO classes — ГИБКИЕ ПОЛЯ под разные варианты API
    private class TenderplanApiResponse
    {
        public List<TenderplanApiDto>? Tenders { get; set; }
        public List<TenderplanApiDto>? Items { get; set; }
        public List<TenderplanApiDto>? Data { get; set; }
        public List<TenderplanApiDto>? Result { get; set; }
        public List<TenderplanApiDto>? List { get; set; }
        public List<TenderplanApiDto>? Records { get; set; }
    }

    private class TenderplanApiDto
    {
        // 🔹 ID / номер тендера (несколько возможных названий)
        public string? Id { get; set; }
        public string? TenderId { get; set; }
        public string? Number { get; set; }
        public string? RegNumber { get; set; }
        public string? ProcurementNumber { get; set; }
        public string? NoticeNumber { get; set; }

        // 🔹 Название / предмет закупки
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? ObjectName { get; set; }
        public string? Description { get; set; }
        public string? Subject { get; set; }

        // 🔹 Цена (несколько возможных названий)
        public decimal? InitialPrice { get; set; }
        public decimal? Price { get; set; }
        public decimal? Nmc { get; set; }
        public decimal? NmcSum { get; set; }
        public decimal? StartPrice { get; set; }

        // 🔹 Валюта
        public string? Currency { get; set; }
        public string? CurrencyCode { get; set; }

        // 🔹 Даты (несколько возможных названий)
        public DateTime? PublishDate { get; set; }
        public DateTime? PublicationDate { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? Deadline { get; set; }
        public DateTime? SubmissionCloseDate { get; set; }
        public DateTime? ApplicationEndDate { get; set; }
        public long? PublicationDateTime { get; set; } // Unix timestamp

        // 🔹 Статус / этап
        public string? Status { get; set; }
        public string? Stage { get; set; }
        public string? PublicationStatus { get; set; }
        public string? ProcedureState { get; set; }

        // 🔹 Прочее
        public string? LawType { get; set; } // "44", "223"
        public int? PlacingWay { get; set; } // ID способа размещения
        public string? Customer { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerInn { get; set; }
    }
}