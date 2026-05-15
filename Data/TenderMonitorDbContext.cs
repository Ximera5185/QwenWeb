using Microsoft.EntityFrameworkCore;
using QwenWeb.Models;

namespace QwenWeb.Data;

public class TenderMonitorDbContext : DbContext
{
    public TenderMonitorDbContext(DbContextOptions<TenderMonitorDbContext> options) : base(options) { }

    public DbSet<TenderMonitorRecord> Tenders { get; set; } = null!;
    public DbSet<MonitorProfile> MonitorProfiles { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 🔹 первичный ключ на Id, а не на Link
        modelBuilder.Entity<TenderMonitorRecord>()
            .HasKey(t => t.Id);

        modelBuilder.Entity<TenderMonitorRecord>()
            .Property(t => t.CreatedAtUtc)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<MonitorProfile>()
            .Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
        modelBuilder.Entity<MonitorProfile>()
            .Property(p => p.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<TenderMonitorRecord>()
            .HasOne<MonitorProfile>()
            .WithMany()
            .HasForeignKey(t => t.ProfileId)
            .OnDelete(DeleteBehavior.SetNull);

        // 🔹 составной уникальный индекс: одна закупка в одном профиле
        modelBuilder.Entity<TenderMonitorRecord>()
            .HasIndex(t => new { t.RegNumber, t.ProfileId })
            .IsUnique();
    }
}