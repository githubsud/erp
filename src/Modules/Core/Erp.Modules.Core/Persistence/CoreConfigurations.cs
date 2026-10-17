using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.Modules.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Core.Persistence;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b) => b.ToTable("tenant", "core");
}

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("company", "core");
        b.Property(x => x.ForeignOwnershipPct).HasPrecision(5, 2);
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b) => b.ToTable("branch", "core");
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("app_user", "core");
        b.Property(x => x.Version).HasXminConcurrency();
        b.Property(x => x.MfaSecret).HasColumnName("mfa_secret_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.MfaPendingSecret).HasColumnName("mfa_pending_encrypted").HasConversion(new EncryptedStringConverter());
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_token", "core");
        b.Property(x => x.TokenHash).IsFixedLength();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("role", "core");
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b) => b.ToTable("role_permission", "core");
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b) => b.ToTable("user_role", "core");
}
