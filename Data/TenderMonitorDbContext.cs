using Microsoft.EntityFrameworkCore;
using QwenWeb.Models;
using System.Reflection.Emit;

namespace QwenWeb.Data;

public class TenderMonitorDbContext : DbContext
{
    public TenderMonitorDbContext(DbContextOptions<TenderMonitorDbContext> options) : base(options) { }

    public DbSet<TenderMonitorRecord> Tenders { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenderMonitorRecord>()
            .HasKey(t => t.Link);

        modelBuilder.Entity<TenderMonitorRecord>()
            .Property(t => t.CreatedAtUtc)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}
