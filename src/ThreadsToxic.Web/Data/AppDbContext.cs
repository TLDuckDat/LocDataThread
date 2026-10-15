using Microsoft.EntityFrameworkCore;

namespace ThreadsToxic.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<ReviewQueueItem> ReviewQueue => Set<ReviewQueueItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(e =>
        {
            e.ToTable("comments");
            e.HasKey(c => c.Id);
            e.Property(c => c.AnalysisData).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Post>(e =>
        {
            e.ToTable("posts");
            e.HasKey(p => p.Id);
        });

        modelBuilder.Entity<ReviewQueueItem>(e =>
        {
            e.ToTable("review_queue");
            e.HasKey(r => r.Id);
            e.Property(r => r.AnalysisData).HasColumnType("jsonb");
        });
    }
}
