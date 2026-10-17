using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.Modules.HR.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.HR.Persistence;

internal sealed class HrSettingsConfiguration : IEntityTypeConfiguration<HrSettings>
{
    public void Configure(EntityTypeBuilder<HrSettings> b)
    {
        b.ToTable("settings", "hr");
        foreach (var rate in new[]
                 {
                     nameof(HrSettings.GratuityDaysPerYear), nameof(HrSettings.GratuityDivisor), nameof(HrSettings.DailyRateDivisor),
                     nameof(HrSettings.HoursPerDay), nameof(HrSettings.OvertimeNormalRate), nameof(HrSettings.OvertimeNightRate),
                     nameof(HrSettings.OvertimeRestDayRate), nameof(HrSettings.OvertimeHolidayRate), nameof(HrSettings.DeductionCapPercent),
                     nameof(HrSettings.SiEmployeeRatePercent), nameof(HrSettings.SiEmployerRatePercent)
                 })
            b.Property(rate).HasPrecision(9, 4);
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b) => b.ToTable("department", "hr");
}

internal sealed class PayComponentConfiguration : IEntityTypeConfiguration<PayComponent>
{
    public void Configure(EntityTypeBuilder<PayComponent> b) => b.ToTable("pay_component", "hr");
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employee", "hr");
        b.Property(x => x.Qid).HasColumnName("qid_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.PassportNo).HasColumnName("passport_no_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.Iban).HasColumnName("iban_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.QidHash).IsFixedLength();
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class EmployeeDocumentConfiguration : IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<EmployeeDocument> b) => b.ToTable("employee_document", "hr");
}

internal sealed class CompanyDocumentConfiguration : IEntityTypeConfiguration<CompanyDocument>
{
    public void Configure(EntityTypeBuilder<CompanyDocument> b) => b.ToTable("company_document", "hr");
}

internal sealed class EmploymentContractConfiguration : IEntityTypeConfiguration<EmploymentContract>
{
    public void Configure(EntityTypeBuilder<EmploymentContract> b)
    {
        b.ToTable("employment_contract", "hr");
        b.Property(x => x.GratuityDaysPerYear).HasPrecision(9, 4);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.ContractId);
    }
}

internal sealed class ContractComponentConfiguration : IEntityTypeConfiguration<ContractComponent>
{
    public void Configure(EntityTypeBuilder<ContractComponent> b) => b.ToTable("contract_component", "hr");
}

internal sealed class LeaveTypeConfiguration : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> b) => b.ToTable("leave_type", "hr");
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.ToTable("leave_request", "hr");
        b.Property(x => x.Days).HasPrecision(9, 2);
    }
}

internal sealed class PayrollInputConfiguration : IEntityTypeConfiguration<PayrollInput>
{
    public void Configure(EntityTypeBuilder<PayrollInput> b)
    {
        b.ToTable("payroll_input", "hr");
        b.Property(x => x.Hours).AsQuantity();
    }
}

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.ToTable("payroll_run", "hr");
        b.Property(x => x.Version).HasXminConcurrency();
    }
}

internal sealed class PayslipConfiguration : IEntityTypeConfiguration<Payslip>
{
    public void Configure(EntityTypeBuilder<Payslip> b)
    {
        b.ToTable("payslip", "hr");
        b.Property(x => x.Qid).HasColumnName("qid_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.Iban).HasColumnName("iban_encrypted").HasConversion(new EncryptedStringConverter());
        b.Property(x => x.PaidDays).HasPrecision(9, 2);
        b.Property(x => x.UnpaidDays).HasPrecision(9, 2);
        b.Property(x => x.HalfPayDays).HasPrecision(9, 2);
        b.Property(x => x.OvertimeHours).AsQuantity();
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PayslipId);
    }
}

internal sealed class PayslipLineConfiguration : IEntityTypeConfiguration<PayslipLine>
{
    public void Configure(EntityTypeBuilder<PayslipLine> b)
    {
        b.ToTable("payslip_line", "hr");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.Rate).AsQuantity();
    }
}

internal sealed class GratuityAccrualConfiguration : IEntityTypeConfiguration<GratuityAccrual>
{
    public void Configure(EntityTypeBuilder<GratuityAccrual> b)
    {
        b.ToTable("gratuity_accrual", "hr");
        b.Property(x => x.DaysPerYear).HasPrecision(9, 4);
    }
}

internal sealed class WpsFileConfiguration : IEntityTypeConfiguration<WpsFile>
{
    public void Configure(EntityTypeBuilder<WpsFile> b)
    {
        b.ToTable("wps_file", "hr");
        b.Property(x => x.Sha256).IsFixedLength();
    }
}

internal sealed class FinalSettlementConfiguration : IEntityTypeConfiguration<FinalSettlement>
{
    public void Configure(EntityTypeBuilder<FinalSettlement> b)
    {
        b.ToTable("final_settlement", "hr");
        b.Property(x => x.GratuityDaysPerYear).HasPrecision(9, 4);
        b.Property(x => x.LeaveBalanceDays).HasPrecision(9, 2);
        b.Property(x => x.DailyLeaveRate).AsQuantity();
        b.Property(x => x.UnpaidSalaryDays).HasPrecision(9, 2);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.FinalSettlementId);
    }
}

internal sealed class FinalSettlementLineConfiguration : IEntityTypeConfiguration<FinalSettlementLine>
{
    public void Configure(EntityTypeBuilder<FinalSettlementLine> b)
    {
        b.ToTable("final_settlement_line", "hr");
        b.Property(x => x.Quantity).AsQuantity();
        b.Property(x => x.Rate).AsQuantity();
    }
}
