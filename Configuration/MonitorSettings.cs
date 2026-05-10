using System;

namespace QwenWeb.Configuration;

public class MonitorSettings
{
    private readonly object _lock = new object();
    private int _pollIntervalMinutes = 2; // 👈 Дефолт: 2 минуты (минимум)

    public string RssUrl { get; set; } = string.Empty;
    public int HttpClientTimeoutMinutes { get; set; } = 2;
    public int DatabaseCommandTimeoutSeconds { get; set; } = 30;
    public bool AutoLoadDocuments { get; set; } = false;
    public int PollIntervalMinutes
    {
        get { lock (_lock) return _pollIntervalMinutes; }
        set { lock (_lock) _pollIntervalMinutes = value; }
    }
}