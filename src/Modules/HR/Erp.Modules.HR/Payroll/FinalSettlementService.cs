using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Payroll;

public sealed record CreateSettlementRequest(
    DateOnly LastWorkingDay,
    SettlementReason Reason,
    decimal NoticePay = 0,
    decimal AirTicket = 0,
    decimal OtherEarnings = 0,
    decimal OtherDeductions = 0,
    decimal? AdvanceRecovery = null,
    string? Notes = null);

/// <summary>
/// Final settlement lifecycle: Draft (calculated, can be deleted and recreated) → Posted (journal entry, employee
/// terminated, contract ended) → Paid; a posted settlement can be reversed, which restores the employee.
/// </summary>
internal sealed class FinalSettlementService(
    ErpDbContext db,
    ITenantContext tenant,
    INumberingService numbering,
    IPostingService posting,
    IFinanceQueries finance,
    IOutbox outbox,
    IClock clock,
    HrSettingsProvider settingsProvider,
    PayrollService payroll)
{
    public const string SourceType = "final_settlement";
    public const string PaymentSourceType = "final_settlement_payment";

    public Task<FinalSettlement> CreateAsync(Guid employeeId, CreateSettlementRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId, token)
                ?? throw new NotFoundException("Employee", employeeId);
            if (r.LastWorkingDay < employee.JoiningDate)
                throw new BusinessRuleException("hr.settlement_date", "The last working day is before the joining date.", "آخر يوم عمل يسبق تاريخ الالتحاق.");
            if (await db.Set<FinalSettlement>().AnyAsync(s => s.EmployeeId == employeeId && s.Status != SettlementStatus.Reversed, token))
                throw new ConflictException("hr.settlement_exists", "This employee already has a final settlement.", "لدى هذا الموظف مخالصة نهائية مسبقاً.");

            var settings = await settingsProvider.GetAsync(token);
            var contracts = await payroll.LoadContractsAsync([employeeId], r.LastWorkingDay, r.LastWorkingDay, token);
            if (!contracts.TryGetValue(employeeId, out var contract))
                throw new BusinessRuleException("hr.settlement_no_contract", "No employment contract covers the last working day.", "لا يوجد عقد عمل يغطي آخر يوم عمل.");
            var components = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == companyId).ToDictionaryAsync(c => c.Id, token);

            var gratuityBasis = contract.Components
                .Where(c => components[c.PayComponentId].WpsField == WpsField.Basic
                            || (contract.GratuityBasis == GratuityBasis.BasicPlusFixedAllowances && components[c.PayComponentId].InGratuityBasis))
                .Sum(c => c.Amount);
            var leaveBasis = contract.Components
                .Where(c => settings.LeaveSalaryBasis == GratuityBasis.Basic
                    ? components[c.PayComponentId].WpsField == WpsField.Basic
                    : components[c.PayComponentId].IsFixed && components[c.PayComponentId].Kind == ComponentKind.Earning)
                .Sum(c => c.Amount);

            var excluded = (await payroll.LoadServiceExclusionsAsync([employeeId], r.LastWorkingDay, token)).GetValueOrDefault(employeeId);
            var leaveTaken = await (from l in db.Set<LeaveRequest>()
                                    join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                                    where l.EmployeeId == employeeId && l.Status == LeaveStatus.Approved && t.DeductsAnnualBalance && l.FromDate <= r.LastWorkingDay
                                    select l.Days).SumAsync(token);

            var lastPayrollEnd = await (from s in db.Set<Payslip>()
                                        join run in db.Set<PayrollRun>() on s.PayrollRunId equals run.Id
                                        where s.EmployeeId == employeeId && (run.Status == PayrollStatus.Posted || run.Status == PayrollStatus.Paid)
                                        select (DateOnly?)run.PeriodEnd).MaxAsync(token);
            var unpaidFrom = lastPayrollEnd?.AddDays(1) ?? employee.JoiningDate;
            if (unpaidFrom < employee.JoiningDate) unpaidFrom = employee.JoiningDate;
            var unpaidLeave = unpaidFrom <= r.LastWorkingDay
                ? await (from l in db.Set<LeaveRequest>()
                         join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                         where l.EmployeeId == employeeId && l.Status == LeaveStatus.Approved && !t.IsPaid
                               && l.FromDate <= r.LastWorkingDay && l.ToDate >= unpaidFrom
                         select new { l.FromDate, l.ToDate }).ToListAsync(token)
                : [];

            var provision = await GratuityProvisionAsync(employee, token);
            var advances = r.AdvanceRecovery ?? Math.Max(0,
                await finance.GetPartyBalanceAsync(AccountRoles.EmployeeAdvances, PartyType.Employee, employeeId, r.LastWorkingDay, token));

            var result = SettlementCalculator.Calculate(new SettlementInput(
                employee.JoiningDate, r.LastWorkingDay, excluded,
                employee.SocialInsuranceScheme == SocialInsuranceScheme.None,
                gratuityBasis, contract.GratuityDaysPerYear ?? settings.GratuityDaysPerYear, settings.GratuityDivisor, settings.GratuityMinServiceMonths,
                GratuityCalculator.AnnualLeaveDaysPerYear(employee.JoiningDate, r.LastWorkingDay, contract.AnnualLeaveDays), leaveTaken,
                leaveBasis, settings.DailyRateDivisor,
                unpaidFrom <= r.LastWorkingDay ? unpaidFrom : null,
                unpaidLeave.Sum(l => PayrollCalculator.OverlapDays(l.FromDate, l.ToDate, unpaidFrom, r.LastWorkingDay)),
                contract.Components.Where(c => components[c.PayComponentId].IsFixed)
                    .Select(c => new FixedPay(new ComponentRef(c.PayComponentId, components[c.PayComponentId].Code, components[c.PayComponentId].NameEn,
                        components[c.PayComponentId].NameAr, ComponentKind.Earning, components[c.PayComponentId].WpsField, true), c.Amount)).ToList(),
                r.NoticePay, r.AirTicket, r.OtherEarnings, advances, r.OtherDeductions));

            if (result.NetPayable < 0)
                throw new BusinessRuleException("hr.settlement_negative_net",
                    $"Deductions ({result.TotalDeductions:N2}) exceed the employee's dues ({result.TotalEarnings:N2}).",
                    "الاستقطاعات تتجاوز مستحقات الموظف.");

            var settlement = new FinalSettlement
            {
                CompanyId = companyId,
                EmployeeId = employeeId,
                SettlementNo = await numbering.NextAsync(SourceType, "FS", r.LastWorkingDay, ct: token),
                LastWorkingDay = r.LastWorkingDay,
                Reason = r.Reason,
                JoiningDate = employee.JoiningDate,
                ServiceDays = result.ServiceDays,
                ExcludedDays = excluded,
                BasisWage = gratuityBasis,
                GratuityDaysPerYear = contract.GratuityDaysPerYear ?? settings.GratuityDaysPerYear,
                EligibleForGratuity = result.EligibleForGratuity,
                GratuityAmount = result.GratuityAmount,
                GratuityProvision = employee.SocialInsuranceScheme == SocialInsuranceScheme.None ? provision : 0,
                LeaveBalanceDays = result.LeaveBalanceDays,
                DailyLeaveRate = result.DailyLeaveRate,
                UnpaidSalaryFrom = unpaidFrom <= r.LastWorkingDay ? unpaidFrom : null,
                UnpaidSalaryDays = result.UnpaidSalaryDays,
                TotalEarnings = result.TotalEarnings,
                TotalDeductions = result.TotalDeductions,
                NetPayable = result.NetPayable,
                Notes = r.Notes,
                EmployeeWasActive = employee.Status == EmployeeStatus.Active
            };
            settlement.Lines.AddRange(result.Lines.Select((l, i) => new FinalSettlementLine
            {
                LineNo = i + 1,
                Code = l.Code,
                NameEn = l.NameEn,
                NameAr = l.NameAr,
                Kind = l.Kind,
                PayComponentId = l.PayComponentId,
                Quantity = l.Quantity,
                Rate = l.Rate,
                Amount = l.Amount
            }));
            db.Add(settlement);
            return settlement;
        }, ct);

    public async Task DeleteDraftAsync(Guid id, CancellationToken ct)
    {
        var settlement = await LoadAsync(id, ct);
        RequireStatus(settlement, SettlementStatus.Draft);
        db.Remove(settlement);
        await db.SaveChangesAsync(ct);
    }

    public Task<FinalSettlement> PostAsync(Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var s = await LoadAsync(id, token);
            RequireStatus(s, SettlementStatus.Draft);
            if (await (from p in db.Set<Payslip>()
                       join run in db.Set<PayrollRun>() on p.PayrollRunId equals run.Id
                       where p.EmployeeId == s.EmployeeId && run.Status == PayrollStatus.Approved
                       select p.Id).AnyAsync(token))
                throw new BusinessRuleException("hr.settlement_payroll_pending",
                    "An approved payroll that includes this employee is waiting to be posted. Post or recalculate it first.",
                    "توجد مسيرة رواتب معتمدة تشمل هذا الموظف بانتظار الترحيل.");

            var journal = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType,
                SourceId = s.Id,
                SourceNo = s.SettlementNo,
                PostingDate = s.LastWorkingDay,
                MemoEn = $"Final settlement {s.SettlementNo}",
                MemoAr = $"مخالصة نهائية {s.SettlementNo}",
                Lines = BuildPostingLines(s)
            }, token);

            var employee = await db.Set<Employee>().SingleAsync(e => e.Id == s.EmployeeId, token);
            employee.Status = EmployeeStatus.Terminated;
            employee.TerminationDate = s.LastWorkingDay;
            var contract = await db.Set<EmploymentContract>().SingleOrDefaultAsync(c => c.EmployeeId == s.EmployeeId && c.Status == ContractStatus.Active, token);
            if (contract is not null)
            {
                contract.Status = ContractStatus.Ended;
                contract.EndDate = s.LastWorkingDay;
                s.EndedContractId = contract.Id;
            }

            s.Status = SettlementStatus.Posted;
            s.PostedAt = clock.UtcNow;
            s.JournalEntryId = journal.JournalEntryId;
            outbox.Enqueue("hr.final_settlement_posted", new { settlementId = s.Id, s.SettlementNo, s.EmployeeId, s.NetPayable });
            return s;
        }, ct);

    public Task<FinalSettlement> MarkPaidAsync(Guid id, MarkPaidRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var s = await LoadAsync(id, token);
            RequireStatus(s, SettlementStatus.Posted);
            var account = await finance.FindPostableAccountAsync(r.PaymentAccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("hr.payment_account", "Choose an active bank or cash account.", "اختر حساب بنك أو صندوق نشط.");
            if (r.PaymentDate < s.LastWorkingDay)
                throw new BusinessRuleException("hr.payment_date", "Payment cannot be dated before the last working day.", "لا يمكن أن يسبق تاريخ الصرف آخر يوم عمل.");

            if (s.NetPayable > 0)
            {
                var payment = await posting.PostAsync(new PostingRequest
                {
                    SourceType = PaymentSourceType,
                    SourceId = s.Id,
                    SourceNo = s.SettlementNo,
                    PostingDate = r.PaymentDate,
                    MemoEn = $"Final settlement payment {s.SettlementNo}",
                    MemoAr = $"صرف المخالصة النهائية {s.SettlementNo}",
                    Lines =
                    [
                        new PostingLine { AccountRole = AccountRoles.SalariesPayable, Side = PostingSide.Debit, Amount = s.NetPayable },
                        new PostingLine { AccountId = account.Id, Side = PostingSide.Credit, Amount = s.NetPayable }
                    ]
                }, token);
                s.PaymentJournalEntryId = payment.JournalEntryId;
            }
            s.Status = SettlementStatus.Paid;
            s.PaidAt = clock.UtcNow;
            s.PaymentDate = r.PaymentDate;
            s.PaymentAccountId = account.Id;
            return s;
        }, ct);

    public Task<FinalSettlement> ReverseAsync(Guid id, ReversePayrollRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var s = await LoadAsync(id, token);
            if (s.Status == SettlementStatus.Paid)
                throw new BusinessRuleException("hr.settlement_paid", "A paid settlement cannot be reversed.", "لا يمكن عكس مخالصة مدفوعة.");
            RequireStatus(s, SettlementStatus.Posted);

            await posting.ReverseAsync(SourceType, s.Id, r.Date, r.Reason, token);
            if (s.EmployeeWasActive)
            {
                var employee = await db.Set<Employee>().SingleAsync(e => e.Id == s.EmployeeId, token);
                employee.Status = EmployeeStatus.Active;
                employee.TerminationDate = null;
            }
            if (s.EndedContractId is { } contractId)
            {
                var contract = await db.Set<EmploymentContract>().SingleAsync(c => c.Id == contractId, token);
                contract.Status = ContractStatus.Active;
                contract.EndDate = null;
            }
            s.Status = SettlementStatus.Reversed;
            return s;
        }, ct);

    /// <summary>
    /// Gratuity releases the provision already booked and expenses only the difference (positive or negative);
    /// other dues go to their expense roles, recoveries to advances / other income, the net to salaries payable.
    /// </summary>
    internal static List<PostingLine> BuildPostingLines(FinalSettlement s)
    {
        var lines = new List<PostingLine>();
        var party = (Type: (PartyType?)PartyType.Employee, Id: (Guid?)s.EmployeeId);

        if (s.GratuityProvision > 0)
            lines.Add(new PostingLine { AccountRole = AccountRoles.GratuityProvision, Side = PostingSide.Debit, Amount = s.GratuityProvision, Description = "Release gratuity provision" });
        var gratuityDifference = s.GratuityAmount - s.GratuityProvision;
        if (gratuityDifference != 0)
            lines.Add(new PostingLine
            {
                AccountRole = AccountRoles.GratuityExpense,
                Side = gratuityDifference > 0 ? PostingSide.Debit : PostingSide.Credit,
                Amount = Math.Abs(gratuityDifference),
                Description = "Gratuity difference on settlement"
            });

        foreach (var l in s.Lines.Where(l => l.Code != SettlementCalculator.Gratuity))
        {
            lines.Add(l.Code switch
            {
                SettlementCalculator.LeaveEncashment => new PostingLine { AccountRole = AccountRoles.LeaveSalaryExpense, Side = PostingSide.Debit, Amount = l.Amount, Description = l.NameEn },
                SettlementCalculator.AdvanceRecovery => new PostingLine
                {
                    AccountRole = AccountRoles.EmployeeAdvances, Side = PostingSide.Credit, Amount = l.Amount, PartyType = party.Type, PartyId = party.Id, Description = l.NameEn
                },
                SettlementCalculator.ExcessLeave or SettlementCalculator.OtherDeduction =>
                    new PostingLine { AccountRole = AccountRoles.PayrollDeduction, Side = PostingSide.Credit, Amount = l.Amount, Description = l.NameEn },
                _ => new PostingLine
                {
                    AccountRole = AccountRoles.PayrollExpense, RoleKey = l.PayComponentId, Side = PostingSide.Debit, Amount = l.Amount, Description = l.NameEn
                }
            });
        }

        if (s.NetPayable > 0)
            lines.Add(new PostingLine { AccountRole = AccountRoles.SalariesPayable, Side = PostingSide.Credit, Amount = s.NetPayable, Description = "Final settlement payable" });
        return lines;
    }

    private async Task<decimal> GratuityProvisionAsync(Employee employee, CancellationToken ct)
    {
        var latest = await db.Set<GratuityAccrual>().AsNoTracking()
            .Where(a => a.EmployeeId == employee.Id && a.Status == AccrualStatus.Posted)
            .OrderByDescending(a => a.PeriodEnd)
            .Select(a => (decimal?)a.Liability)
            .FirstOrDefaultAsync(ct);
        return latest ?? employee.OpeningGratuityLiability;
    }

    internal async Task<FinalSettlement> LoadAsync(Guid id, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM hr.final_settlement WHERE id = {id} FOR UPDATE", ct);
        return await db.Set<FinalSettlement>().Include(s => s.Lines)
                   .SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
               ?? throw new NotFoundException("Final settlement", id);
    }

    private static void RequireStatus(FinalSettlement s, SettlementStatus required)
    {
        if (s.Status != required)
            throw new BusinessRuleException("hr.settlement_status", $"Settlement {s.SettlementNo} is {s.Status}; this action needs {required}.",
                $"حالة المخالصة {s.SettlementNo} لا تسمح بهذا الإجراء.");
    }
}
