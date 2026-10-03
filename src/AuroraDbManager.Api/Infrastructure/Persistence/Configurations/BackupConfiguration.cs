using AuroraDbManager.Api.Domain.Backups;
using AuroraDbManager.Api.Domain.Databases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class BackupConfiguration : IEntityTypeConfiguration<Backup>
{
    public void Configure(EntityTypeBuilder<Backup> builder)
    {
        builder.ToTable("backups", table =>
        {
            table.HasCheckConstraint("ck_backups_status", $"status IN ({EnumStorage.SqlValues<BackupStatus>()})");
            table.HasCheckConstraint("ck_backups_storage_type", $"storage_type IN ({EnumStorage.SqlValues<BackupStorageType>()})");
            table.HasCheckConstraint("ck_backups_size_bytes", "size_bytes IS NULL OR size_bytes > 0");
            // A checksum and its algorithm come together, only on a completed backup. A completed
            // backup may lack both: those completed before checksums existed.
            table.HasCheckConstraint(
                "ck_backups_checksum",
                $"(checksum IS NULL) = (checksum_algorithm IS NULL) AND (checksum IS NULL OR (status = '{EnumStorage.ToDbValue(BackupStatus.Completed)}' AND length(checksum) = {Backup.ChecksumLength}))");
            table.HasCheckConstraint(
                "ck_backups_checksum_algorithm",
                $"checksum_algorithm IS NULL OR checksum_algorithm IN ({EnumStorage.SqlValues<BackupChecksumAlgorithm>()})");
            // Only a completed backup has an artifact, and a completed backup always has one.
            table.HasCheckConstraint(
                "ck_backups_artifact",
                $"(status = '{EnumStorage.ToDbValue(BackupStatus.Completed)}') = (path IS NOT NULL AND size_bytes IS NOT NULL)");
        });

        builder.HasKey(b => b.Id).HasName("pk_backups");

        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.DatabaseId).HasColumnName("database_id");
        // Concurrency token: a status change is rejected if the row's status changed since it was loaded.
        builder.Property(b => b.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<BackupStatus>(v))
            .IsConcurrencyToken();
        builder.Property(b => b.StorageType).HasColumnName("storage_type").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<BackupStorageType>(v));
        builder.Property(b => b.Path).HasColumnName("path").HasMaxLength(Backup.PathMaxLength);
        builder.Property(b => b.SizeBytes).HasColumnName("size_bytes");
        builder.Property(b => b.ChecksumAlgorithm).HasColumnName("checksum_algorithm").HasMaxLength(16)
            .HasConversion(
                v => EnumStorage.ToDbValue(v!.Value),
                v => EnumStorage.FromDbValue<BackupChecksumAlgorithm>(v));
        builder.Property(b => b.Checksum).HasColumnName("checksum").HasMaxLength(Backup.ChecksumLength);
        builder.Property(b => b.CreatedAt).HasColumnName("created_at");
        builder.Property(b => b.CompletedAt).HasColumnName("completed_at");
        builder.Property(b => b.ErrorCode).HasColumnName("error_code").HasMaxLength(Backup.ErrorCodeMaxLength);
        builder.Property(b => b.ErrorMessage).HasColumnName("error_message").HasMaxLength(Backup.ErrorMessageMaxLength);

        // A backup's metadata goes with its database, and so with its instance: the lifecycle
        // removes database rows outright, and a backup row cannot outlive the row it refers to.
        // The artifact itself is not removed; nothing deletes backup files in this phase.
        builder.HasOne<Database>()
            .WithMany()
            .HasForeignKey(b => b.DatabaseId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_backups_databases_database_id");

        // Serves the per-database listing, newest first.
        builder.HasIndex(b => new { b.DatabaseId, b.CreatedAt }).HasDatabaseName("ix_backups_database_id_created_at");
    }
}
