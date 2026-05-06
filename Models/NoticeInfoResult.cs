using System;

namespace QwenWeb.Models;

public class NoticeInfoResult
{
    public string? NoticeInfoId { get; set; }
    public string? LawType { get; set; } // "notice223", "notice44", "notice615"
    public string? DocumentsPageUrl { get; set; }
    public bool IsValid => !string.IsNullOrEmpty(NoticeInfoId) && !string.IsNullOrEmpty(LawType);
}