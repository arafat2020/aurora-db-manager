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
            // Enabled with a port, or disabled without one; never anything in between.
            table.HasCheckConstraint(
                "ck_instances_external_access",
                "(external_access_enabled AND external_port IS NOT NULL) OR (NOT external_access_enabled AND external_port IS NULL)");
            table.HasCheckConstraint(
                "ck_instances_external_port",
                $"external_port IS NULL OR (external_port >= {Instance.MinExternalPort} AND external_port <= {Instance.MaxExternalPort})");
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
        builder.Property(i => i.ErrorCode).HasColumnName("error_code").HasMaxLength(Instance.ErrorCodeMaxLength);
        builder.Property(i => i.ErrorMessage).HasColumnName("error_message").HasMaxLength(Instance.ErrorMessageMaxLength);

        // Concurrency token: of two requests that change an instance's external access at the same
        // time, the second finds it changed and is refused.
        builder.Property(i => i.ExternalAccessEnabled).HasColumnName("external_access_enabled")
            .HasDefaultValue(false)
            .IsConcurrencyToken();
        builder.Property(i => i.ExternalPort).HasColumnName("external_port");

        builder.HasIndex(i => i.CreatedAt).HasDatabaseName("ix_instances_created_at");
        // One host port, one instance: what makes allocating a port safe against a concurrent allocation.
        builder.HasIndex(i => i.ExternalPort).HasDatabaseName("ux_instances_external_port").IsUnique();
    }
}
