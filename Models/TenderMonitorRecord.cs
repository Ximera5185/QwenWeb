using System;
using System.ComponentModel.DataAnnotations;

namespace QwenWeb.Models;

public class TenderMonitorRecord
{
    [Key]
    public string Link { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? PubDate { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}