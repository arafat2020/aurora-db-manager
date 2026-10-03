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
            // Database jobs name their database; every other job has none.
            table.HasCheckConstraint(
                "ck_jobs_database_id",
                $"(type IN ({DatabaseJobTypes}) AND database_id IS NOT NULL) OR (type NOT IN ({DatabaseJobTypes}) AND database_id IS NULL)");
        });

        builder.HasKey(j => j.Id).HasName("pk_jobs");

        builder.Property(j => j.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(j => j.Type).HasColumnName("type").HasMaxLength(32)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<JobType>(v));
        // Concurrency token: of two executions that both load a pending job, only one can start it.
        // Together with the lease id below, every update of a job is conditional on the job still
        // being in the state, and under the lease, it was loaded with.
        builder.Property(j => j.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<JobStatus>(v))
            .IsConcurrencyToken();
        builder.Property(j => j.InstanceId).HasColumnName("instance_id");
        // Not a foreign key: a delete_database job removes its database and must outlive it.
        builder.Property(j => j.DatabaseId).HasColumnName("database_id");
        builder.Property(j => j.Attempt).HasColumnName("attempt");
        builder.Property(j => j.MaxAttempts).HasColumnName("max_attempts");
        builder.Property(j => j.ErrorCode).HasColumnName("error_code").HasMaxLength(Job.ErrorCodeMaxLength);
        builder.Property(j => j.ErrorMessage).HasColumnName("error_message").HasMaxLength(Job.ErrorMessageMaxLength);
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.StartedAt).HasColumnName("started_at");
        builder.Property(j => j.CompletedAt).HasColumnName("completed_at");
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at");
        // Concurrency token: an execution whose lease was taken over can no longer update the job.
        builder.Property(j => j.LeaseId).HasColumnName("lease_id").IsConcurrencyToken();
        builder.Property(j => j.LeaseExpiresAt).HasColumnName("lease_expires_at");

        builder.Ignore(j => j.HasAttemptsRemaining);

        // Deleting an instance removes its jobs.
        builder.HasOne<Instance>()
            .WithMany()
            .HasForeignKey(j => j.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_jobs_instances_instance_id");

        builder.HasIndex(j => j.InstanceId).HasDatabaseName("ix_jobs_instance_id");
        builder.HasIndex(j => j.Status).HasDatabaseName("ix_jobs_status");

        // An instance has at most one unfinished job of a type, however many processes try to create one.
        // Database jobs are left out: an instance may be working on several of its databases at once.
        builder.HasIndex(j => new { j.InstanceId, j.Type })
            .IsUnique()
            .HasFilter($"{Unfinished} AND database_id IS NULL")
            .HasDatabaseName("ux_jobs_instance_id_type_unfinished");

        // A database has at most one unfinished job of any type: a create and a delete, or two of
        // either, can never be in progress for the same database.
        builder.HasIndex(j => j.DatabaseId)
            .IsUnique()
            .HasFilter($"{Unfinished} AND database_id IS NOT NULL")
            .HasDatabaseName("ux_jobs_database_id_unfinished");
    }

    private static readonly string Unfinished =
        $"status IN ('{EnumStorage.ToDbValue(JobStatus.Pending)}', '{EnumStorage.ToDbValue(JobStatus.Running)}')";

    private static readonly string DatabaseJobTypes =
        $"'{EnumStorage.ToDbValue(JobType.CreateDatabase)}', '{EnumStorage.ToDbValue(JobType.DeleteDatabase)}'";
}
