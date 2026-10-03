using AuroraDbManager.Api.Domain.Databases;
using AuroraDbManager.Api.Domain.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class DatabaseConfiguration : IEntityTypeConfiguration<Database>
{
    public void Configure(EntityTypeBuilder<Database> builder)
    {
        builder.ToTable("databases", table =>
        {
            table.HasCheckConstraint("ck_databases_status", $"status IN ({EnumStorage.SqlValues<DatabaseStatus>()})");
        });

        builder.HasKey(d => d.Id).HasName("pk_databases");

        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(d => d.InstanceId).HasColumnName("instance_id");
        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(DatabaseName.MaxLength).IsRequired();
        // Concurrency token: a status change is rejected if the row's status changed since it was loaded.
        builder.Property(d => d.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<DatabaseStatus>(v))
            .IsConcurrencyToken();
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");
        builder.Property(d => d.ErrorCode).HasColumnName("error_code").HasMaxLength(Database.ErrorCodeMaxLength);
        builder.Property(d => d.ErrorMessage).HasColumnName("error_message").HasMaxLength(Database.ErrorMessageMaxLength);

        // Deleting an instance removes the metadata of its databases. The databases themselves go
        // with the instance's data volume.
        builder.HasOne<Instance>()
            .WithMany()
            .HasForeignKey(d => d.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_databases_instances_instance_id");

        builder.HasIndex(d => d.InstanceId).HasDatabaseName("ix_databases_instance_id");

        // A name is unique within its instance, however many requests try to create it at once.
        builder.HasIndex(d => new { d.InstanceId, d.Name })
            .IsUnique()
            .HasDatabaseName("ux_databases_instance_id_name");
    }
}
