using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QwenWeb.Models;

namespace QwenWeb.Services.Tenderplan;

/// <summary>
/// Единый контракт для получения списка тендеров из внешнего источника.
/// Позволяет в будущем реализовать стратегию переключения (RSS / Tenderplan / Mock).
/// </summary>
public interface ITenderSourceProvider
{
    /// <summary>
    /// Асинхронно получает новые записи тендеров с поддержкой отмены операции.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены для безопасного прерывания.</param>
    /// <returns>Только для чтения список записей.</returns>
    Task<IReadOnlyList<TenderplanRecord>> FetchAsync(CancellationToken cancellationToken);
}