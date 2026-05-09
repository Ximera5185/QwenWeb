// File: Data/TenderplanDbContext.cs
using Microsoft.EntityFrameworkCore;
using QwenWeb.Models;

namespace QwenWeb.Data;

/// <summary>
/// Контекст базы данных для мониторинга через Tenderplan API.
/// Полностью изолирован от TenderMonitorDbContext (RSS).
/// Использует отдельную таблицу "TenderplanRecords" во избежание конфликтов.
/// </summary>
public class TenderplanDbContext : DbContext
{
    // 🔹 Private readonly fields (none)

    // 🔹 Public properties
    public DbSet<TenderplanRecord> TenderplanRecords { get; set; } = null!;

    // 🔹 Constructors
    public TenderplanDbContext(DbContextOptions<TenderplanDbContext> options) : base(options) { }

    // 🔹 Public methods (none)

    // 🔹 Protected/Override methods
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 🔹 Явно указываем имя таблицы, чтобы избежать конфликта с TenderMonitorDbContext
        modelBuilder.Entity<TenderplanRecord>()
            .ToTable("TenderplanRecords")
            .HasKey(t => t.TenderId);

        modelBuilder.Entity<TenderplanRecord>()
            .Property(t => t.CreatedAtUtc)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<TenderplanRecord>()
            .Property(t => t.Source)
            .HasDefaultValue("TENDERPLAN")
            .IsRequired();
    }

    // 🔹 Private helpers (none)
}