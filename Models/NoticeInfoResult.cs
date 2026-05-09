using System;

namespace QwenWeb.Models;

public class NoticeInfoResult
{
    public string? NoticeInfoId { get; set; }
    public string? LawType { get; set; } // "notice223", "notice44", "notice615"
    public string? DocumentsPageUrl { get; set; }

    // 👇 Новое свойство для поддержки новой структуры ЕИС
    public string? RegNumber { get; set; }

    // IsValid теперь истинно, если есть либо NoticeInfoId, либо RegNumber + LawType
    public bool IsValid => (!string.IsNullOrEmpty(NoticeInfoId) || !string.IsNullOrEmpty(RegNumber))
                          && !string.IsNullOrEmpty(LawType);
}
