using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using AuroraDbManager.Api.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;

namespace AuroraDbManager.Api.Infrastructure.Persistence;

/// <summary>EF Core context for the system metadata database.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Database> Databases => Set<Database>();
    public DbSet<Backup> Backups => Set<Backup>();
    public DbSet<InstanceSecret> InstanceSecrets => Set<InstanceSecret>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
