using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace QwenWeb.Models;

[Table("Tenders")]
[Index(nameof(RegNumber), nameof(ProfileId), IsUnique = true)]  // 🔹 составной уникальный индекс
public class TenderMonitorRecord
{
    // 🔹 автоинкрементный первичный ключ
    [Key]
    public int Id { get; set; }

    // 🔹 уникальный номер закупки — часть составного ключа
    [Required, MaxLength(50)]
    public string RegNumber { get; set; } = string.Empty;

    // 🔹 детальная ссылка (универсальный шаблон) — без UNIQUE
    [Required, MaxLength(500)]
    public string Link { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }
    public DateTime? PubDate { get; set; }

    // 🔹 поля для обогащённых данных
    public decimal? InitialPrice { get; set; }
    public string? CustomerName { get; set; }

    // 🔹 статус обработки
    [MaxLength(20)]
    public string Status { get; set; } = "raw";

    public string? LastError { get; set; }

    // 🔹 привязка к профилю — часть составного ключа
    public int? ProfileId { get; set; }

    // 🔹 метаданные
    public string? RegionCode { get; set; }
    public string? LawType { get; set; }
    public string? SearchKeywords { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}