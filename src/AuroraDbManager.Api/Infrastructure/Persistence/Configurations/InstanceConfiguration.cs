using AuroraDbManager.Api.Domain.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class InstanceConfiguration : IEntityTypeConfiguration<Instance>
{
    public void Configure(EntityTypeBuilder<Instance> builder)
    {
        builder.ToTable("instances", table =>
        {
            table.HasCheckConstraint("ck_instances_engine", $"engine IN ({EnumStorage.SqlValues<InstanceEngine>()})");
            table.HasCheckConstraint("ck_instances_status", $"status IN ({EnumStorage.SqlValues<InstanceStatus>()})");
            table.HasCheckConstraint("ck_instances_cpu", "cpu > 0");
            table.HasCheckConstraint("ck_instances_memory_mb", "memory_mb > 0");
            table.HasCheckConstraint("ck_instances_storage_gb", "storage_gb > 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_instances");

        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(i => i.Name).HasColumnName("name").HasMaxLength(Instance.NameMaxLength).IsRequired();
        builder.Property(i => i.Engine).HasColumnName("engine").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<InstanceEngine>(v));
        builder.Property(i => i.Version).HasColumnName("version").HasMaxLength(Instance.VersionMaxLength).IsRequired();
        // Concurrency token: a status change is rejected if the row's status changed since it was loaded.
        builder.Property(i => i.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<InstanceStatus>(v))
            .IsConcurrencyToken();
        builder.Property(i => i.Cpu).HasColumnName("cpu");
        builder.Property(i => i.MemoryMb).HasColumnName("memory_mb");
        builder.Property(i => i.StorageGb).HasColumnName("storage_gb");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(i => i.CreatedAt).HasDatabaseName("ix_instances_created_at");
    }
}
