using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Finance.Persistence;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("account", "fin");
        b.Property(x => x.Version).HasXminConcurrency();
        b.Ignore(x => x.IsProfitAndLoss);
    }
}

internal sealed class FiscalYearConfiguration : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> b)
    {
        b.ToTable("fiscal_year", "fin");
        b.HasMany(x => x.Periods).WithOne().HasForeignKey(x => x.FiscalYearId);
    }
}

internal sealed class FiscalPeriodConfiguration : IEntityTypeConfiguration<FiscalPeriod>
{
    public void Configure(EntityTypeBuilder<FiscalPeriod> b)
    {
        b.ToTable("fiscal_period", "fin");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class CostCenterConfiguration : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> b) => b.ToTable("cost_center", "fin");
}

internal sealed class PostingRuleConfiguration : IEntityTypeConfiguration<PostingRule>
{
    public void Configure(EntityTypeBuilder<PostingRule> b) => b.ToTable("posting_rule", "fin");
}

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> b)
    {
        b.ToTable("journal_entry", "fin");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.JournalEntryId);
    }
}

internal sealed class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> b)
    {
        b.ToTable("journal_line", "fin");
        b.Property(x => x.FxRate).HasPrecision(19, 8);
    }
}

internal sealed class JournalVoucherConfiguration : IEntityTypeConfiguration<JournalVoucher>
{
    public void Configure(EntityTypeBuilder<JournalVoucher> b) => b.ToTable("journal_voucher", "fin");
}

internal sealed class BankStatementConfiguration : IEntityTypeConfiguration<BankStatement>
{
    public void Configure(EntityTypeBuilder<BankStatement> b)
    {
        b.ToTable("bank_statement", "fin");
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.BankStatementId);
    }
}

internal sealed class BankStatementLineConfiguration : IEntityTypeConfiguration<BankStatementLine>
{
    public void Configure(EntityTypeBuilder<BankStatementLine> b) => b.ToTable("bank_statement_line", "fin");
}

internal sealed class BankMatchConfiguration : IEntityTypeConfiguration<BankMatch>
{
    public void Configure(EntityTypeBuilder<BankMatch> b) => b.ToTable("bank_match", "fin");
}
