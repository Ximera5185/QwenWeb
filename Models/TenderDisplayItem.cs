using System;

namespace QwenWeb.Models;

public class TenderDisplayItem
{
    public QwenWeb.Models.TenderMonitorRecord Record { get; set; } = null!;
    public ParsedTenderInfo ParsedInfo { get; set; } = new ParsedTenderInfo();
}