using System;
using System.ComponentModel.DataAnnotations;

namespace QwenWeb.Models;

/// <summary>
/// Сущность тендера из источника Tenderplan API.
/// Хранится в отдельной БД (tenderplan.db) для полной изоляции данных.
/// </summary>
public class TenderplanRecord
{
    // 🔹 Private fields (none required for pure POCO)

    // 🔹 Public properties
    [Key]
    public string TenderId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? RegNumber { get; set; }

    public decimal? InitialPrice { get; set; }

    public string? Currency { get; set; }

    public DateTime? PublishDateUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Source { get; set; } = "TENDERPLAN";

    // 🔹 Constructors
    public TenderplanRecord() { }

    // 🔹 Public methods
    public override string ToString() => $"[{Source}] {TenderId}: {Title}";

    // 🔹 Private helpers / nested classes (none)
}