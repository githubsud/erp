using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.HR.Payroll;

public enum AdvanceStatus { Posted, Reversed }

/// <summary>A salary advance or loan paid to an employee (debit Employee Advances for that employee, credit bank/cash).</summary>
public sealed class EmployeeAdvance : TenantEntity
{
    public Guid CompanyId { get; init; }
    public Guid EmployeeId { get; init; }
    public required string AdvanceNo { get; init; }
    public DateOnly AdvanceDate { get; init; }
    public decimal Amount { get; init; }
    public decimal? MonthlyRecovery { get; init; }
    public Guid PaymentAccountId { get; init; }
    public AdvanceStatus Status { get; set; } = AdvanceStatus.Posted;
    public string? Notes { get; init; }
    public Guid JournalEntryId { get; set; }
}

internal sealed class EmployeeAdvanceConfiguration : IEntityTypeConfiguration<EmployeeAdvance>
{
    public void Configure(EntityTypeBuilder<EmployeeAdvance> b) => b.ToTable("employee_advance", "hr");
}

public sealed record CreateAdvanceRequest(DateOnly Date, decimal Amount, Guid PaymentAccountId, decimal? MonthlyRecovery, string? Notes);

public sealed record AdvanceDto(Guid Id, string AdvanceNo, Guid EmployeeId, DateOnly AdvanceDate, decimal Amount, decimal? MonthlyRecovery,
    string Status, string? Notes, Guid JournalEntryId);

public sealed record AdvanceBalanceDto(Guid EmployeeId, DateOnly AsOf, decimal Outstanding);

internal sealed class AdvanceService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting, IFinanceQueries finance)
{
    public const string SourceType = "employee_advance";

    public Task<EmployeeAdvance> CreateAsync(Guid employeeId, CreateAdvanceRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId, token)
                ?? throw new NotFoundException("Employee", employeeId);
            if (employee.Status != EmployeeStatus.Active)
                throw new BusinessRuleException("hr.advance_inactive", "Advances can only be given to active employees.", "يمكن صرف السلف للموظفين النشطين فقط.");
            if (r.Amount <= 0)
                throw new BusinessRuleException("hr.advance_amount", "The advance amount must be positive.", "يجب أن يكون مبلغ السلفة موجباً.");
            if (r.MonthlyRecovery is <= 0)
                throw new BusinessRuleException("hr.advance_recovery", "Monthly recovery must be positive.", "يجب أن يكون القسط الشهري موجباً.");
            var account = await finance.FindPostableAccountAsync(r.PaymentAccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("hr.payment_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");

            var advance = new EmployeeAdvance
            {
                CompanyId = companyId,
                EmployeeId = employeeId,
                AdvanceNo = await numbering.NextAsync(SourceType, "ADV", r.Date, ct: token),
                AdvanceDate = r.Date,
                Amount = Money.Round(r.Amount),
                MonthlyRecovery = r.MonthlyRecovery is null ? null : Money.Round(r.MonthlyRecovery.Value),
                PaymentAccountId = account.Id,
                Notes = r.Notes
            };
            var journal = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType,
                SourceId = advance.Id,
                SourceNo = advance.AdvanceNo,
                PostingDate = r.Date,
                MemoEn = $"Salary advance to {employee.EmpNo} {employee.NameEn}",
                MemoAr = $"سلفة راتب للموظف {employee.EmpNo} {employee.NameAr}",
                Lines =
                [
                    new PostingLine
                    {
                        AccountRole = AccountRoles.EmployeeAdvances, Side = PostingSide.Debit, Amount = advance.Amount,
                        PartyType = PartyType.Employee, PartyId = employeeId
                    },
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Credit, Amount = advance.Amount }
                ]
            }, token);
            advance.JournalEntryId = journal.JournalEntryId;
            db.Add(advance);
            return advance;
        }, ct);

    public async Task<AdvanceBalanceDto> BalanceAsync(Guid employeeId, DateOnly asOf, CancellationToken ct) =>
        new(employeeId, asOf, await finance.GetPartyBalanceAsync(AccountRoles.EmployeeAdvances, PartyType.Employee, employeeId, asOf, ct));

    public static AdvanceDto ToDto(EmployeeAdvance a) =>
        new(a.Id, a.AdvanceNo, a.EmployeeId, a.AdvanceDate, a.Amount, a.MonthlyRecovery, a.Status.ToString(), a.Notes, a.JournalEntryId);
}
