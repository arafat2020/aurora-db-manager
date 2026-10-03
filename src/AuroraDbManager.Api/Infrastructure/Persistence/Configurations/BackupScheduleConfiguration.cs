using AuroraDbManager.Api.Domain.BackupSchedules;
using AuroraDbManager.Api.Domain.Databases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class BackupScheduleConfiguration : IEntityTypeConfiguration<BackupSchedule>
{
    public void Configure(EntityTypeBuilder<BackupSchedule> builder)
    {
        builder.ToTable("backup_schedules", table =>
        {
            // A disabled schedule owes no run.
            table.HasCheckConstraint("ck_backup_schedules_next_run_at", "enabled OR next_run_at IS NULL");
        });

        builder.HasKey(s => s.Id).HasName("pk_backup_schedules");

        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.DatabaseId).HasColumnName("database_id");
        builder.Property(s => s.CronExpression).HasColumnName("cron_expression")
            .HasMaxLength(BackupSchedule.CronExpressionMaxLength).IsRequired();
        builder.Property(s => s.TimeZoneId).HasColumnName("time_zone_id")
            .HasMaxLength(BackupSchedule.TimeZoneIdMaxLength).IsRequired();
        builder.Property(s => s.Enabled).HasColumnName("enabled");
        // Concurrency token: an occurrence is claimed by changing this, and only one of several
        // that try can. The same goes for a change through the API against a claim.
        builder.Property(s => s.NextRunAt).HasColumnName("next_run_at").IsConcurrencyToken();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

        // A schedule goes with its database, and so with its instance; it never keeps one from being deleted.
        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(s => s.DatabaseId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_backup_schedules_databases_database_id");

        // A database has one schedule at most, however many requests try to create one.
        builder.HasIndex(s => s.DatabaseId).IsUnique().HasDatabaseName("ux_backup_schedules_database_id");

        // What the scheduler asks at every pass: which enabled schedules are due.
        builder.HasIndex(s => s.NextRunAt)
            .HasFilter("enabled AND next_run_at IS NOT NULL")
            .HasDatabaseName("ix_backup_schedules_next_run_at_enabled");
    }
}
