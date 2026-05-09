// File: Services/Tenderplan/TenderplanBackgroundService.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QwenWeb.Configuration;
using QwenWeb.Data;
using QwenWeb.Models;

namespace QwenWeb.Services.Tenderplan;

/// <summary>
/// Фоновая служба для периодического опроса Tenderplan API.
/// Полностью изолирована от TenderMonitorBackgroundService (RSS).
/// </summary>
public class TenderplanBackgroundService : BackgroundService
{
    // 🔹 Private readonly fields
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TenderplanBackgroundService> _logger;
    private readonly TenderplanSettings _settings;
    private readonly ITenderSourceProvider _provider;
    private readonly object _statsLock = new object();
    private int _totalPollsCount;
    private int _newTendersFoundLastRun;
    private DateTime _lastRunTimeUtc = DateTime.MinValue;
    private bool _isErrorLastRun;
    private readonly object _delayLock = new object();
    private CancellationTokenSource? _delayCts;

    // 🔹 Public properties
    public int TotalPollsCount { get { lock (_statsLock) return _totalPollsCount; } }
    public int NewTendersFoundLastRun { get { lock (_statsLock) return _newTendersFoundLastRun; } }
    public DateTime LastRunTimeUtc { get { lock (_statsLock) return _lastRunTimeUtc; } }
    public bool IsErrorLastRun { get { lock (_statsLock) return _isErrorLastRun; } }

    // 🔹 Constructors
    public TenderplanBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<TenderplanBackgroundService> logger,
        IOptions<TenderplanSettings> settings,
        ITenderSourceProvider provider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    // 🔹 Public methods
    public void ResetDelay()
    {
        lock (_delayLock)
        {
            _delayCts?.Cancel();
        }
    }

    public async Task RunManualAsync()
    {
        await FetchAndSaveAsync(CancellationToken.None);
    }

    // 🔹 Protected/Override methods
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Tenderplan источник отключён в настройках (Enabled: false). Фоновый опрос приостановлен.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        _logger.LogInformation("Запуск фонового опроса Tenderplan API (интервал: {Interval} мин).", _settings.PollIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchAndSaveAsync(stoppingToken);

            int interval = _settings.PollIntervalMinutes;
            CancellationTokenSource delayCts;
            lock (_delayLock)
            {
                delayCts = _delayCts = new CancellationTokenSource();
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(interval), delayCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Интервал изменён или сервис останавливается
            }

            if (stoppingToken.IsCancellationRequested) break;
        }
    }

    // 🔹 Private helpers
    private async Task FetchAndSaveAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        TenderplanDbContext db = scope.ServiceProvider.GetRequiredService<TenderplanDbContext>();

        lock (_statsLock) { _totalPollsCount++; }

        try
        {
            var newRecords = await _provider.FetchAsync(token);
            int foundCount = 0;

            foreach (var record in newRecords)
            {
                // 🔹 ИСПРАВЛЕНО: TenderplanRecords вместо Tenders
                bool exists = await db.TenderplanRecords.AnyAsync(t => t.TenderId == record.TenderId, token);
                if (!exists)
                {
                    // 🔹 ИСПРАВЛЕНО: TenderplanRecords вместо Tenders
                    db.TenderplanRecords.Add(record);
                    foundCount++;
                }
            }

            if (foundCount > 0)
            {
                await db.SaveChangesAsync(token);
                _logger.LogInformation("Сохранено {Count} новых тендеров Tenderplan в tenderplan.db.", foundCount);
            }
            else
            {
                _logger.LogDebug("Новых тендеров Tenderplan не найдено за текущий цикл.");
            }

            UpdateStats(foundCount, false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при опросе Tenderplan API.");
            UpdateStats(0, true);
        }
    }

    private void UpdateStats(int newCount, bool isError)
    {
        lock (_statsLock)
        {
            _newTendersFoundLastRun = newCount;
            _lastRunTimeUtc = DateTime.UtcNow;
            _isErrorLastRun = isError;
        }
    }
}