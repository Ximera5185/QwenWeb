// File: Services/Tenderplan/TenderplanApiProvider.cs
using System;
using System.Collections.Generic;
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
    private const string ApiEndpoint = "/api/tenders/v2/getlist";

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

        string lawTypes = _settings.DefaultFilters.LawTypes.Length > 0
            ? string.Join(",", _settings.DefaultFilters.LawTypes)
            : "44,223";

        string requestUri = $"{ApiEndpoint}?page=1&pageSize={_settings.PageSize}&lawTypes={Uri.EscapeDataString(lawTypes)}";
        _logger.LogDebug("Запрос к Tenderplan API: {Uri}", requestUri);

        using HttpResponseMessage response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var records = ParseApiResponse(json);
        _logger.LogInformation("Получено {Count} тендеров от Tenderplan API.", records.Count);

        return records;
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
            var apiResponse = JsonSerializer.Deserialize<TenderplanApiResponse>(json, options);

            if (apiResponse?.Data == null) return records;

            foreach (var dto in apiResponse.Data)
            {
                if (string.IsNullOrEmpty(dto.Id)) continue;

                records.Add(new TenderplanRecord
                {
                    TenderId = dto.Id,
                    Title = dto.Title ?? string.Empty,
                    RegNumber = dto.RegNumber,
                    InitialPrice = dto.InitialPrice,
                    Currency = dto.Currency ?? "RUB",
                    PublishDateUtc = dto.PublishDate,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Ошибка десериализации ответа Tenderplan API.");
        }

        return records;
    }

    // 🔹 Private nested DTO classes
    private class TenderplanApiResponse
    {
        public List<TenderplanApiDto>? Data { get; set; }
    }

    private class TenderplanApiDto
    {
        public string? Id { get; set; }
        public string? RegNumber { get; set; }
        public string? Title { get; set; }
        public decimal? InitialPrice { get; set; }
        public string? Currency { get; set; }
        public DateTime? PublishDate { get; set; }
    }
}