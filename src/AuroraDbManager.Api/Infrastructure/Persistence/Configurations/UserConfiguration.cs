using AuroraDbManager.Api.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuroraDbManager.Api.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_role", $"role IN ({EnumStorage.SqlValues<UserRole>()})");
        });

        builder.HasKey(u => u.Id).HasName("pk_users");

        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.Username).HasColumnName("username").HasMaxLength(User.UsernameMaxLength).IsRequired();
        builder.Property(u => u.NormalizedUsername).HasColumnName("normalized_username")
            .HasMaxLength(User.UsernameMaxLength).IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(User.PasswordHashMaxLength).IsRequired();
        builder.Property(u => u.Role).HasColumnName("role").HasMaxLength(16)
            .HasConversion(v => EnumStorage.ToDbValue(v), v => EnumStorage.FromDbValue<UserRole>(v));
        builder.Property(u => u.Enabled).HasColumnName("enabled");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at");

        // One user per name, whatever its case, however many requests try to create it.
        builder.HasIndex(u => u.NormalizedUsername).IsUnique().HasDatabaseName("ux_users_normalized_username");
    }
}
