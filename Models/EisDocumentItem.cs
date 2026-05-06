using System;

namespace QwenWeb.Models;

public class EisDocumentItem
{
    public string? FileName { get; set; }
    public string? FileId { get; set; }
    public string? MimeType { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? DownloadUrl { get; set; }

    // 👇 Новые свойства для Этапа 4
    public string? LocalPath { get; set; }
    public double DownloadProgress { get; set; } // 0.0 - 1.0
    public string? Status { get; set; } = "pending"; // pending | downloading | downloaded | skipped | error
    public string? ErrorMessage { get; set; }

    public bool IsArchive { get; set; }
    public string? ExtractedText { get; set; }

    // 👇 Новые свойства для Этапа 5
    public string? ExtractedTextPath { get; set; }
    public double TextExtractionProgress { get; set; } // 0.0 - 1.0
    public string? TextExtractionStatus { get; set; } // pending | extracting | extracted | error
    public string? TextExtractionError { get; set; }
}