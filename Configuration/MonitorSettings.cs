using System;

namespace QwenWeb.Configuration;

public class MonitorSettings
{
    private readonly object _lock = new object();
    private int _pollIntervalMinutes = 30; // 👈 Явная инициализация по умолчанию

    public string RssUrl { get; set; } = string.Empty; // 👈 Защита от CS8618
    public int HttpClientTimeoutMinutes { get; set; } = 2;
    public int DatabaseCommandTimeoutSeconds { get; set; } = 30;

    public int PollIntervalMinutes
    {
        get { lock (_lock) return _pollIntervalMinutes; }
        set { lock (_lock) _pollIntervalMinutes = value; }
    }
}