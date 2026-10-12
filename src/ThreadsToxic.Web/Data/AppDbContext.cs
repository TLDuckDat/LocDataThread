using Microsoft.EntityFrameworkCore;

namespace ThreadsToxic.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Comment> Comments => Set<Comment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(e =>
        {
            e.ToTable("comments");
            e.HasKey(c => c.Id);
            e.Property(c => c.AnalysisData).HasColumnType("jsonb");
        });
    }
}
