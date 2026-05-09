// File: Configuration/TenderplanSettings.cs
using System;

namespace QwenWeb.Configuration;

/// <summary>
/// Настройки интеграции с API Tenderplan.
/// Чтение только через IConfiguration. Секреты (AccessToken) — только через UserSecrets / env.
/// </summary>
public class TenderplanSettings
{
    private readonly object _lock = new object();
    private int _pollIntervalMinutes = 5;
    private int _pageSize = 50;

    /// <summary>Базовый URL API. По умолчанию: https://tenderplan.ru</summary>
    public string BaseUrl { get; set; } = "https://tenderplan.ru";

    /// <summary>Версия API. По умолчанию: 3.5.0</summary>
    public string ApiVersion { get; set; } = "3.5.0";

    /// <summary>
    /// Интервал опроса источника (минуты).
    /// Минимальное значение: 2 (защита от слишком частых запросов).
    /// </summary>
    public int PollIntervalMinutes
    {
        get { lock (_lock) return _pollIntervalMinutes; }
        set { lock (_lock) _pollIntervalMinutes = Math.Max(2, value); }
    }

    /// <summary>Размер страницы для пагинации (по умолчанию 50). Максимум: 100.</summary>
    public int PageSize
    {
        get { lock (_lock) return _pageSize; }
        set { lock (_lock) _pageSize = Math.Clamp(value, 10, 100); }
    }

    /// <summary>Флаг включения/выключения источника. По умолчанию: false (источник отключён).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Фильтры по умолчанию для запроса к API.
    /// Заполняются из appsettings.json, могут переопределяться через код.
    /// </summary>
    public TenderplanFilterDefaults DefaultFilters { get; set; } = new TenderplanFilterDefaults();
}

/// <summary>
/// Вспомогательный класс для фильтров по умолчанию.
/// </summary>
public class TenderplanFilterDefaults
{
    /// <summary>Типы законов: "44", "223", "615". Пустой массив = все типы.</summary>
    public string[] LawTypes { get; set; } = Array.Empty<string>();

    /// <summary>Минимальная цена контракта (нуль = без ограничения).</summary>
    public decimal? MinPrice { get; set; }

    /// <summary>Регионы (коды ФИАС/КЛАДР). Пустой массив = все регионы.</summary>
    public string[] Regions { get; set; } = Array.Empty<string>();

    /// <summary>Ключевые слова для полнотекстового поиска (опционально).</summary>
    public string[] Keywords { get; set; } = Array.Empty<string>();
}