using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs", table =>
        {
            table.HasCheckConstraint("ck_jobs_type", $"type IN ({EnumStorage.SqlValues<JobType>()})");
            table.HasCheckConstraint("ck_jobs_status", $"status IN ({EnumStorage.SqlValues<JobStatus>()})");
            table.HasCheckConstraint("ck_jobs_max_attempts", "max_attempts > 0");
            table.HasCheckConstraint("ck_jobs_attempt", "attempt >= 0 AND attempt <= max_attempts");
        });

        builder.HasKey(j => j.Id).HasName("pk_jobs");

        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(j => j.Type).HasColumnName("type").HasMaxLength(32)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<JobType>(v));
        // Concurrency token: of two executions that both load a pending job, only one can start it.
        builder.Property(j => j.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<JobStatus>(v))
            .IsConcurrencyToken();
        builder.Property(j => j.InstanceId).HasColumnName("instance_id");
        builder.Property(j => j.Attempt).HasColumnName("attempt");
        builder.Property(j => j.MaxAttempts).HasColumnName("max_attempts");
        builder.Property(j => j.ErrorCode).HasColumnName("error_code").HasMaxLength(Job.ErrorCodeMaxLength);
        builder.Property(j => j.ErrorMessage).HasColumnName("error_message").HasMaxLength(Job.ErrorMessageMaxLength);
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.StartedAt).HasColumnName("started_at");
        builder.Property(j => j.CompletedAt).HasColumnName("completed_at");
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at");

        builder.Ignore(j => j.HasAttemptsRemaining);

        // Deleting an instance removes its jobs.
        builder.HasOne<Instance>()
            .WithMany()
            .HasForeignKey(j => j.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_jobs_instances_instance_id");

        builder.HasIndex(j => j.InstanceId).HasDatabaseName("ix_jobs_instance_id");
        builder.HasIndex(j => j.Status).HasDatabaseName("ix_jobs_status");
    }
}
