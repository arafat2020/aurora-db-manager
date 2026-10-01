using AuroraDbManager.Api.Domain.Instances;
using AuroraDbManager.Api.Infrastructure.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class InstanceSecretConfiguration : IEntityTypeConfiguration<InstanceSecret>
{
    public void Configure(EntityTypeBuilder<InstanceSecret> builder)
    {
        builder.ToTable("instance_secrets");

        builder.HasKey(s => s.InstanceId).HasName("pk_instance_secrets");

        builder.Property(s => s.InstanceId).HasColumnName("instance_id").ValueGeneratedNever();
        builder.Property(s => s.ProtectedAdminPassword).HasColumnName("protected_admin_password")
            .HasMaxLength(1024).IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        // Deleting an instance removes its secrets.
        builder.HasOne<Instance>()
            .WithOne()
            .HasForeignKey<InstanceSecret>(s => s.InstanceId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_instance_secrets_instances_instance_id");
    }
}
