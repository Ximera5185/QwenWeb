using System;
using System.ComponentModel.DataAnnotations;

namespace QwenWeb.Models;

public class MonitorProfile
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    // 🔹 новое поле: ссылка-шаблон из веб-интерфейса еис
    // именно по ней будет работать браузерный скрапер
    [Required, MaxLength(2000)]
    public string SearchUrl { get; set; } = string.Empty;

    // 🔹 старые поля — теперь опциональны
    // нужны для отображения в интерфейсе и обратной совместимости
    // новые профили могут не заполнять их (вся логика в SearchUrl)
    public string? RegionCode { get; set; }
    public string? LawType { get; set; }

    public int PollIntervalMinutes { get; set; } = 1440; // по умолчанию раз в сутки

    // 🔹 НОВОЕ: режим подстановки даты
    /// <summary>
    /// Если true — используется CustomDate, иначе — DateTime.Today
    /// </summary>
    public bool UseCustomDate { get; set; } = false;

    /// <summary>
    /// Пользовательская дата для подстановки в SearchUrl (если UseCustomDate = true)
    /// </summary>
    public DateTime? CustomDate { get; set; }


    public DateTime? LastRunAt { get; set; }
    public int LastFoundCount { get; set; }
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}