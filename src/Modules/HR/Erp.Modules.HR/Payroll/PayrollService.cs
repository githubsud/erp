using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Wps;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.Payroll;

public sealed record CreatePayrollRunRequest(int Year, int Month);
public sealed record MarkPaidRequest(Guid PaymentAccountId, DateOnly PaymentDate);
public sealed record ReversePayrollRequest(DateOnly Date, string Reason);

/// <summary>
/// Payroll run lifecycle: Draft → Calculated → Approved → Posted → Paid (or Reversed from Posted).
/// Calculation is repeatable until approval; posting writes the salary journal and the monthly gratuity accrual
/// through the central posting service in one transaction.
/// </summary>
internal sealed class PayrollService(
    ErpDbContext db,
    ITenantContext tenant,
    INumberingService numbering,
    IPostingService posting,
    IFinanceQueries finance,
    IOutbox outbox,
    IClock clock,
    HrSettingsProvider settingsProvider)
{
    public const string SourceType = "payroll_run";
    public const string GratuitySourceType = "payroll_gratuity";
    public const string PaymentSourceType = "payroll_payment";

    public Task<PayrollRun> CreateAsync(CreatePayrollRunRequest request, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (request.Month is < 1 or > 12 || request.Year is < 2000 or > 2100)
                throw new BusinessRuleException("hr.invalid_period", "Invalid payroll year or month.", "سنة أو شهر الرواتب غير صحيح.");
            var companyId = tenant.RequireCompanyId();
            if (await db.Set<PayrollRun>().AnyAsync(r => r.CompanyId == companyId && r.Year == request.Year && r.Month == request.Month
                                                         && r.Status != PayrollStatus.Reversed, token))
                throw new ConflictException("hr.payroll_exists", $"A payroll run already exists for {request.Year}-{request.Month:D2}.",
                    $"توجد مسيرة رواتب لشهر {request.Year}-{request.Month:D2} مسبقاً.");

            var settings = await settingsProvider.GetAsync(token);
            var start = new DateOnly(request.Year, request.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var due = end.AddDays(1);
            var run = new PayrollRun
            {
                CompanyId = companyId,
                RunNo = await numbering.NextAsync(SourceType, "PR", end, ct: token),
                Year = request.Year,
                Month = request.Month,
                PeriodStart = start,
                PeriodEnd = end,
                DueDate = due,
                WpsDeadline = due.AddDays(settings.WpsDepositDays)
            };
            db.Add(run);
            return run;
        }, ct);

    public Task<PayrollRun> CalculateAsync(Guid runId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            RequireStatus(run, PayrollStatus.Draft, PayrollStatus.Calculated);
            var settings = await settingsProvider.GetAsync(token);
            var policy = PayrollPolicy.From(settings);

            await db.Set<Payslip>().Where(p => p.PayrollRunId == run.Id).ExecuteDeleteAsync(token);

            var employees = await db.Set<Employee>().AsNoTracking()
                .Where(e => e.CompanyId == run.CompanyId && e.JoiningDate <= run.PeriodEnd
                            && (e.Status == EmployeeStatus.Active || e.TerminationDate >= run.PeriodStart))
                .OrderBy(e => e.EmpNo)
                .ToListAsync(token);
            // Employees whose final settlement is posted were paid up to their last day there.
            var settled = await db.Set<FinalSettlement>().AsNoTracking()
                .Where(s => s.CompanyId == run.CompanyId && (s.Status == SettlementStatus.Posted || s.Status == SettlementStatus.Paid))
                .Select(s => s.EmployeeId).ToListAsync(token);
            employees = employees.Where(e => !settled.Contains(e.Id)).ToList();
            var employeeIds = employees.Select(e => e.Id).ToList();
            var contracts = await LoadContractsAsync(employeeIds, run.PeriodStart, run.PeriodEnd, token);
            var components = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == run.CompanyId).ToDictionaryAsync(c => c.Id, token);
            var inputs = await db.Set<PayrollInput>().AsNoTracking()
                .Where(i => i.Year == run.Year && i.Month == run.Month && employeeIds.Contains(i.EmployeeId)).ToListAsync(token);
            var unpaidLeave = await LoadLeaveDaysAsync(employeeIds, run.PeriodStart, run.PeriodEnd, paidLeave: false, token);

            var errors = new List<string>();
            var payslips = new List<Payslip>();
            foreach (var employee in employees)
            {
                if (!contracts.TryGetValue(employee.Id, out var contract))
                {
                    errors.Add($"{employee.EmpNo} {employee.NameEn}: no active employment contract.");
                    continue;
                }

                var input = new EmployeePayInput(
                    run.PeriodStart, run.PeriodEnd, employee.JoiningDate, employee.TerminationDate,
                    contract.Components.Where(c => components[c.PayComponentId].IsFixed)
                        .Select(c => new FixedPay(Ref(components[c.PayComponentId]), c.Amount)).ToList(),
                    inputs.Where(i => i.EmployeeId == employee.Id && i.Amount is not null)
                        .Select(i => new VariablePay(Ref(components[i.PayComponentId]), i.Amount!.Value)).ToList(),
                    inputs.Where(i => i.EmployeeId == employee.Id && i.Hours is not null)
                        .Select(i => new OvertimeHours(Ref(components[i.PayComponentId]), i.OvertimeType!.Value, i.Hours!.Value)).ToList(),
                    unpaidLeave.GetValueOrDefault(employee.Id));

                var result = PayrollCalculator.Calculate(input, policy);
                if (!result.IsPayable) continue;
                errors.AddRange(result.Errors.Select(e => $"{employee.EmpNo} {employee.NameEn}: {e}"));

                var warnings = result.Warnings.ToList();
                if (employee.InWps && (employee.Iban is null || !QatarIdentifiers.IsValidQatarIban(employee.Iban)))
                    warnings.Add("Missing or invalid IBAN: the employee cannot be paid through WPS.");

                var payslip = new Payslip
                {
                    PayrollRunId = run.Id,
                    EmployeeId = employee.Id,
                    EmpNo = employee.EmpNo,
                    NameEn = employee.NameEn,
                    NameAr = employee.NameAr,
                    CostCenterId = employee.CostCenterId,
                    Qid = employee.Qid,
                    VisaId = employee.VisaId,
                    BankShortName = employee.BankShortName,
                    Iban = employee.Iban,
                    InWps = employee.InWps,
                    PaidDays = result.PaidDays,
                    UnpaidDays = result.UnpaidDays,
                    BasicSalary = result.BasicSalary,
                    OvertimeHours = result.OvertimeHours,
                    Gross = result.Gross,
                    Deductions = result.Deductions,
                    Net = result.Net,
                    Warnings = warnings.Count == 0 ? null : string.Join(" | ", warnings)
                };
                payslip.Lines.AddRange(result.Lines.Select((l, i) => new PayslipLine
                {
                    LineNo = i + 1,
                    PayComponentId = l.Component.Id,
                    Code = l.Component.Code,
                    NameEn = l.Component.NameEn,
                    NameAr = l.Component.NameAr,
                    Kind = l.Component.Kind,
                    WpsField = l.Component.WpsField,
                    Quantity = l.Quantity,
                    Rate = l.Rate,
                    Amount = l.Amount
                }));
                payslips.Add(payslip);
            }

            if (errors.Count > 0)
                throw new BusinessRuleException("hr.payroll_errors", $"Payroll cannot be calculated: {errors.Count} problem(s) need fixing.",
                    $"لا يمكن احتساب الرواتب: يوجد {errors.Count} خطأ يجب تصحيحه.")
                { Details = new Dictionary<string, object?> { ["errors"] = errors } };

            db.AddRange(payslips);
            run.EmployeeCount = payslips.Count;
            run.TotalGross = payslips.Sum(p => p.Gross);
            run.TotalDeductions = payslips.Sum(p => p.Deductions);
            run.TotalNet = payslips.Sum(p => p.Net);
            run.Status = PayrollStatus.Calculated;
            run.CalculatedBy = tenant.ActingUserId;
            run.CalculatedAt = clock.UtcNow;
            return run;
        }, ct);

    public Task<PayrollRun> ApproveAsync(Guid runId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            RequireStatus(run, PayrollStatus.Calculated);
            if (run.EmployeeCount == 0)
                throw new BusinessRuleException("hr.payroll_empty", "There are no payslips to approve.", "لا توجد قسائم رواتب للاعتماد.");
            run.Status = PayrollStatus.Approved;
            run.ApprovedBy = tenant.ActingUserId;
            run.ApprovedAt = clock.UtcNow;
            return run;
        }, ct);

    public Task<PayrollRun> PostAsync(Guid runId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            RequireStatus(run, PayrollStatus.Approved);
            var settings = await settingsProvider.GetAsync(token);
            var payslips = await db.Set<Payslip>().AsNoTracking().Include(p => p.Lines).Where(p => p.PayrollRunId == run.Id).ToListAsync(token);
            var components = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == run.CompanyId).ToDictionaryAsync(c => c.Id, token);

            var salary = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType,
                SourceId = run.Id,
                SourceNo = run.RunNo,
                PostingDate = run.PeriodEnd,
                MemoEn = $"Payroll {run.Year}-{run.Month:D2}",
                MemoAr = $"رواتب شهر {run.Year}-{run.Month:D2}",
                Lines = BuildSalaryLines(payslips, components)
            }, token);

            var accruals = await BuildGratuityAccrualsAsync(run, payslips, settings, token);
            db.AddRange(accruals);
            var gratuityLines = BuildGratuityLines(accruals, payslips);
            if (gratuityLines.Count > 0)
            {
                var gratuity = await posting.PostAsync(new PostingRequest
                {
                    SourceType = GratuitySourceType,
                    SourceId = run.Id,
                    SourceNo = run.RunNo,
                    PostingDate = run.PeriodEnd,
                    MemoEn = $"End of service benefit accrual {run.Year}-{run.Month:D2}",
                    MemoAr = $"استحقاق مكافأة نهاية الخدمة {run.Year}-{run.Month:D2}",
                    Lines = gratuityLines
                }, token);
                run.GratuityJournalEntryId = gratuity.JournalEntryId;
            }

            run.JournalEntryId = salary.JournalEntryId;
            run.TotalGratuityAccrual = accruals.Sum(a => a.Delta);
            run.Status = PayrollStatus.Posted;
            run.PostedAt = clock.UtcNow;
            outbox.Enqueue("hr.payroll_posted", new { runId = run.Id, run.RunNo, run.Year, run.Month, run.TotalNet, run.WpsDeadline });
            return run;
        }, ct);

    public Task<WpsFile> GenerateWpsAsync(Guid runId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            RequireStatus(run, PayrollStatus.Posted, PayrollStatus.Paid);
            var settings = await settingsProvider.GetAsync(token);
            var payslips = await db.Set<Payslip>().AsNoTracking().Include(p => p.Lines)
                .Where(p => p.PayrollRunId == run.Id && p.InWps && p.Net > 0).OrderBy(p => p.EmpNo).ToListAsync(token);

            var header = new SifHeader(settings.EmployerEid ?? "", settings.PayerEid ?? settings.EmployerEid ?? "", settings.PayerQid,
                settings.PayerBankShortName ?? "", settings.PayerIban ?? "", run.Year, run.Month, clock.UtcNow.ToOffset(TimeSpan.FromHours(3)));
            var records = payslips.Select(ToSifRecord).ToList();

            var problems = SifGenerator.Validate(header, records);
            if (problems.Count > 0)
                throw new BusinessRuleException("hr.wps_invalid", $"The WPS file cannot be generated: {problems.Count} problem(s).",
                    $"لا يمكن إنشاء ملف حماية الأجور: يوجد {problems.Count} خطأ.")
                { Details = new Dictionary<string, object?> { ["errors"] = problems } };

            var sif = SifGenerator.Generate(header, records, settings.SifFormatVersion);
            var file = new WpsFile
            {
                PayrollRunId = run.Id,
                FormatVersion = settings.SifFormatVersion,
                PayerBankShortName = header.PayerBankShortName.ToUpperInvariant(),
                FileName = sif.FileName,
                Content = sif.Content,
                Sha256 = sif.Sha256,
                TotalSalaries = sif.TotalSalaries,
                RecordCount = sif.RecordCount
            };
            db.Add(file);
            return file;
        }, ct);

    public Task<PayrollRun> MarkPaidAsync(Guid runId, MarkPaidRequest request, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            RequireStatus(run, PayrollStatus.Posted);
            var account = await finance.FindPostableAccountAsync(request.PaymentAccountId, token);
            if (account is null || account.Subtype is not ("bank" or "cash"))
                throw new BusinessRuleException("hr.payment_account", "Choose an active bank or cash account to pay salaries from.",
                    "اختر حساب بنك أو صندوق نشط لصرف الرواتب.");
            if (request.PaymentDate < run.PeriodEnd)
                throw new BusinessRuleException("hr.payment_date", "Salaries cannot be paid before the end of the payroll month.",
                    "لا يمكن صرف الرواتب قبل نهاية شهر الرواتب.");

            var payment = await posting.PostAsync(new PostingRequest
            {
                SourceType = PaymentSourceType,
                SourceId = run.Id,
                SourceNo = run.RunNo,
                PostingDate = request.PaymentDate,
                MemoEn = $"Salary payment {run.Year}-{run.Month:D2} via {account.Code}",
                MemoAr = $"صرف رواتب {run.Year}-{run.Month:D2}",
                Lines =
                [
                    new PostingLine { AccountRole = AccountRoles.SalariesPayable, Side = PostingSide.Debit, Amount = run.TotalNet },
                    new PostingLine { AccountId = account.Id, Side = PostingSide.Credit, Amount = run.TotalNet }
                ]
            }, token);

            run.Status = PayrollStatus.Paid;
            run.PaidAt = clock.UtcNow;
            run.PaymentDate = request.PaymentDate;
            run.PaymentAccountId = account.Id;
            run.PaymentJournalEntryId = payment.JournalEntryId;
            if (request.PaymentDate > run.WpsDeadline)
                outbox.Enqueue("hr.wps_deadline_missed", new { runId = run.Id, run.RunNo, run.WpsDeadline, request.PaymentDate });
            return run;
        }, ct);

    public Task<PayrollRun> ReverseAsync(Guid runId, ReversePayrollRequest request, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var run = await LoadRunAsync(runId, token);
            if (run.Status == PayrollStatus.Paid)
                throw new BusinessRuleException("hr.payroll_paid", "A paid payroll cannot be reversed. Record corrections in the next payroll.",
                    "لا يمكن عكس مسيرة رواتب مدفوعة. سجل التصحيحات في المسيرة التالية.");
            RequireStatus(run, PayrollStatus.Posted);

            await posting.ReverseAsync(SourceType, run.Id, request.Date, request.Reason, token);
            if (run.GratuityJournalEntryId is not null)
                await posting.ReverseAsync(GratuitySourceType, run.Id, request.Date, request.Reason, token);

            await db.Set<GratuityAccrual>().Where(a => a.PayrollRunId == run.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AccrualStatus.Reversed)
                    .SetProperty(a => a.UpdatedAt, clock.UtcNow).SetProperty(a => a.UpdatedBy, tenant.ActingUserId), token);
            run.Status = PayrollStatus.Reversed;
            return run;
        }, ct);

    /// <summary>Earnings by component and cost centre; deductions to their roles; net to salaries payable.</summary>
    internal static List<PostingLine> BuildSalaryLines(IReadOnlyList<Payslip> payslips, IReadOnlyDictionary<Guid, PayComponent> components)
    {
        var lines = new List<PostingLine>();
        var flat = payslips.SelectMany(p => p.Lines.Select(l => (Payslip: p, Line: l))).ToList();

        foreach (var g in flat.Where(x => x.Line.Kind == ComponentKind.Earning)
                     .GroupBy(x => (x.Line.PayComponentId, x.Payslip.CostCenterId)))
        {
            var component = components[g.Key.PayComponentId];
            lines.Add(new PostingLine
            {
                AccountRole = component.PostingRole,
                RoleKey = component.Id,
                Side = PostingSide.Debit,
                Amount = g.Sum(x => x.Line.Amount),
                CostCenterId = g.Key.CostCenterId,
                Description = component.NameEn
            });
        }

        foreach (var g in flat.Where(x => x.Line.Kind == ComponentKind.Deduction)
                     .GroupBy(x => (x.Line.PayComponentId, Employee: components[x.Line.PayComponentId].PostingRole == AccountRoles.EmployeeAdvances
                         ? x.Payslip.EmployeeId : (Guid?)null)))
        {
            var component = components[g.Key.PayComponentId];
            lines.Add(new PostingLine
            {
                AccountRole = component.PostingRole,
                RoleKey = component.Id,
                Side = PostingSide.Credit,
                Amount = g.Sum(x => x.Line.Amount),
                PartyType = g.Key.Employee is null ? null : PartyType.Employee,
                PartyId = g.Key.Employee,
                Description = component.NameEn
            });
        }

        lines.Add(new PostingLine
        {
            AccountRole = AccountRoles.SalariesPayable,
            Side = PostingSide.Credit,
            Amount = payslips.Sum(p => p.Net),
            Description = "Net salaries payable"
        });
        return lines;
    }

    private async Task<List<GratuityAccrual>> BuildGratuityAccrualsAsync(PayrollRun run, IReadOnlyList<Payslip> payslips,
        HrSettings settings, CancellationToken ct)
    {
        var employeeIds = payslips.Select(p => p.EmployeeId).ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var contracts = await LoadContractsAsync(employeeIds, run.PeriodStart, run.PeriodEnd, ct);
        var components = await db.Set<PayComponent>().AsNoTracking().Where(c => c.CompanyId == run.CompanyId).ToDictionaryAsync(c => c.Id, ct);
        var excluded = await LoadServiceExclusionsAsync(employeeIds, run.PeriodEnd, ct);
        var previous = (await db.Set<GratuityAccrual>().AsNoTracking()
                .Where(a => employeeIds.Contains(a.EmployeeId) && a.Status == AccrualStatus.Posted && a.PeriodEnd < run.PeriodEnd)
                .Select(a => new { a.EmployeeId, a.PeriodEnd, a.Liability })
                .ToListAsync(ct))
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, g => g.MaxBy(a => a.PeriodEnd)!.Liability);

        var accruals = new List<GratuityAccrual>();
        foreach (var payslip in payslips)
        {
            var employee = employees[payslip.EmployeeId];
            if (employee.SocialInsuranceScheme != SocialInsuranceScheme.None) continue; // GRSIA / GCC schemes replace gratuity
            var contract = contracts[employee.Id];

            var basis = contract.Components
                .Where(c => components[c.PayComponentId].WpsField == WpsField.Basic
                            || (contract.GratuityBasis == GratuityBasis.BasicPlusFixedAllowances && components[c.PayComponentId].InGratuityBasis))
                .Sum(c => c.Amount);
            var daysPerYear = contract.GratuityDaysPerYear ?? settings.GratuityDaysPerYear;
            var asOf = employee.TerminationDate is { } t && t < run.PeriodEnd ? t : run.PeriodEnd;
            var serviceDays = GratuityCalculator.ServiceDays(employee.JoiningDate, asOf, excluded.GetValueOrDefault(employee.Id));
            var liability = GratuityCalculator.Liability(basis, daysPerYear, settings.GratuityDivisor, serviceDays);
            var prior = previous.TryGetValue(employee.Id, out var p) ? p : employee.OpeningGratuityLiability;

            accruals.Add(new GratuityAccrual
            {
                EmployeeId = employee.Id,
                PayrollRunId = run.Id,
                PeriodEnd = run.PeriodEnd,
                BasisWage = basis,
                DaysPerYear = daysPerYear,
                ServiceDays = serviceDays,
                Liability = liability,
                PreviousLiability = prior,
                Delta = liability - prior
            });
        }
        return accruals;
    }

    internal static List<PostingLine> BuildGratuityLines(IReadOnlyList<GratuityAccrual> accruals, IReadOnlyList<Payslip> payslips)
    {
        var costCenters = payslips.ToDictionary(p => p.EmployeeId, p => p.CostCenterId);
        var lines = new List<PostingLine>();
        foreach (var g in accruals.GroupBy(a => costCenters[a.EmployeeId]))
        {
            var delta = g.Sum(a => a.Delta);
            if (delta == 0) continue;
            var increase = delta > 0;
            lines.Add(new PostingLine
            {
                AccountRole = AccountRoles.GratuityExpense,
                Side = increase ? PostingSide.Debit : PostingSide.Credit,
                Amount = Math.Abs(delta),
                CostCenterId = g.Key,
                Description = "End of service benefit expense"
            });
            lines.Add(new PostingLine
            {
                AccountRole = AccountRoles.GratuityProvision,
                Side = increase ? PostingSide.Credit : PostingSide.Debit,
                Amount = Math.Abs(delta),
                Description = "End of service benefit provision"
            });
        }
        return lines;
    }

    private static SifRecord ToSifRecord(Payslip p)
    {
        decimal Sum(WpsField field) => p.Lines.Where(l => l.WpsField == field).Sum(l => l.Amount);
        return new SifRecord(p.Qid, p.VisaId, p.NameEn, p.BankShortName ?? "", p.Iban ?? "", p.PaidDays, p.Net,
            Sum(WpsField.Basic), p.OvertimeHours, Sum(WpsField.ExtraIncome), Sum(WpsField.Deduction),
            Sum(WpsField.Housing), Sum(WpsField.Food), Sum(WpsField.Transport), Sum(WpsField.Overtime),
            p.UnpaidDays > 0 ? $"Unpaid leave {p.UnpaidDays:0.##} days" : null);
    }

    /// <summary>The contract in force for the period: the latest one that started on or before the period end.</summary>
    internal async Task<Dictionary<Guid, EmploymentContract>> LoadContractsAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly periodStart,
        DateOnly periodEnd, CancellationToken ct)
    {
        var candidates = await db.Set<EmploymentContract>().AsNoTracking().Include(c => c.Components)
            .Where(c => employeeIds.Contains(c.EmployeeId) && c.StartDate <= periodEnd && (c.EndDate == null || c.EndDate >= periodStart))
            .ToListAsync(ct);
        return candidates.GroupBy(c => c.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.StartDate).First());
    }

    private async Task<Dictionary<Guid, decimal>> LoadLeaveDaysAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly periodStart, DateOnly periodEnd,
        bool paidLeave, CancellationToken ct)
    {
        var leaves = await (from l in db.Set<LeaveRequest>()
                            join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                            where employeeIds.Contains(l.EmployeeId) && l.Status == LeaveStatus.Approved && t.IsPaid == paidLeave
                                  && l.FromDate <= periodEnd && l.ToDate >= periodStart
                            select new { l.EmployeeId, l.FromDate, l.ToDate }).ToListAsync(ct);
        return leaves.GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => (decimal)g.Sum(l => PayrollCalculator.OverlapDays(l.FromDate, l.ToDate, periodStart, periodEnd)));
    }

    /// <summary>Days of approved leave that do not count as service (unpaid leave), up to <paramref name="asOf"/>.</summary>
    internal async Task<Dictionary<Guid, int>> LoadServiceExclusionsAsync(IReadOnlyCollection<Guid> employeeIds, DateOnly asOf, CancellationToken ct)
    {
        var leaves = await (from l in db.Set<LeaveRequest>()
                            join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                            where employeeIds.Contains(l.EmployeeId) && l.Status == LeaveStatus.Approved && t.AffectsService && l.FromDate <= asOf
                            select new { l.EmployeeId, l.FromDate, l.ToDate }).ToListAsync(ct);
        return leaves.GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(l => PayrollCalculator.OverlapDays(l.FromDate, l.ToDate, DateOnly.MinValue, asOf)));
    }

    private async Task<PayrollRun> LoadRunAsync(Guid runId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM hr.payroll_run WHERE id = {runId} FOR UPDATE", ct);
        return await db.Set<PayrollRun>().SingleOrDefaultAsync(r => r.Id == runId && r.CompanyId == tenant.RequireCompanyId(), ct)
               ?? throw new NotFoundException("Payroll run", runId);
    }

    private static void RequireStatus(PayrollRun run, params PayrollStatus[] allowed)
    {
        if (!allowed.Contains(run.Status))
            throw new BusinessRuleException("hr.payroll_status",
                $"Payroll {run.RunNo} is {run.Status}; this action needs status {string.Join(" or ", allowed)}.",
                $"حالة مسيرة الرواتب {run.RunNo} لا تسمح بهذا الإجراء.");
    }

    private static ComponentRef Ref(PayComponent c) => new(c.Id, c.Code, c.NameEn, c.NameAr, c.Kind, c.WpsField, c.ProrateByDays);
}

/// <summary>Loads (and caches per request) the company's HR settings.</summary>
internal sealed class HrSettingsProvider(ErpDbContext db, ITenantContext tenant)
{
    private HrSettings? _cached;

    public async Task<HrSettings> GetAsync(CancellationToken ct) =>
        _cached ??= await db.Set<HrSettings>().SingleOrDefaultAsync(s => s.CompanyId == tenant.RequireCompanyId(), ct)
                    ?? throw new BusinessRuleException("hr.settings_missing", "HR settings are not configured for this company.",
                        "إعدادات الموارد البشرية غير مهيأة لهذه الشركة.");
}
