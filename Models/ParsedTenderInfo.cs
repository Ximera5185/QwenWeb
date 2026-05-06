using System;

namespace QwenWeb.Models;

public class ParsedTenderInfo
{
    public string? LawType { get; set; }
    public string? Customer { get; set; }
    public decimal? InitialPrice { get; set; }
    public string? Currency { get; set; }
    public string? Stage { get; set; }
    public string? ObjectName { get; set; }
    public DateTime? PublishDate { get; set; }

    // Оставляем оригинал для отладки или передачи в ИИ без потери контекста
    public string? RawDescription { get; set; }
}