using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.Modules.HR.Domain;
using Erp.Modules.HR.Payroll;
using Erp.Modules.HR.Wps;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.HR.People;

public sealed record EmployeeRequest(
    string EmpNo,
    string? NameEn,
    string? NameAr,
    string Nationality,
    Gender Gender,
    DateOnly? DateOfBirth,
    DateOnly JoiningDate,
    Guid? DepartmentId,
    Guid? BranchId,
    Guid? CostCenterId,
    string? JobTitleEn,
    string? JobTitleAr,
    string? Mobile,
    string? Email,
    string? Qid,
    string? VisaId,
    string? PassportNo,
    string? BankShortName,
    string? Iban,
    bool InWps = true,
    SocialInsuranceScheme SocialInsuranceScheme = SocialInsuranceScheme.None,
    decimal OpeningGratuityLiability = 0);

public sealed record TerminateRequest(DateOnly TerminationDate);

public sealed record ContractComponentRequest(string ComponentCode, decimal Amount);

public sealed record ContractRequest(
    DateOnly StartDate,
    DateOnly? EndDate,
    ContractType ContractType,
    int ProbationMonths,
    bool AccommodationProvided,
    bool FoodProvided,
    decimal? GratuityDaysPerYear,
    GratuityBasis GratuityBasis,
    int? AnnualLeaveDays,
    IReadOnlyList<ContractComponentRequest> Components,
    decimal? AnnualAirTicketAmount = null);

public sealed record LeaveRequestCreate(Guid EmployeeId, string LeaveTypeCode, DateOnly FromDate, DateOnly ToDate, string? Reason);

public sealed record PayrollInputRequest(Guid EmployeeId, int Year, int Month, string ComponentCode, OvertimeType? OvertimeType,
    decimal? Hours, decimal? Amount, string? Notes);

internal sealed class EmployeeService(ErpDbContext db, ITenantContext tenant)
{
    public async Task<Employee> CreateAsync(EmployeeRequest r, CancellationToken ct)
    {
        var employee = new Employee
        {
            CompanyId = tenant.RequireCompanyId(),
            EmpNo = Guard.Required(r.EmpNo, "empNo").ToUpperInvariant(),
            NameEn = "",
            NameAr = "",
            Nationality = "",
            JoiningDate = r.JoiningDate
        };
        Apply(employee, r);
        db.Add(employee);
        await SaveAsync(ct);
        return employee;
    }

    public async Task<Employee> UpdateAsync(Guid id, EmployeeRequest r, CancellationToken ct)
    {
        var employee = await FindAsync(id, ct);
        Apply(employee, r);
        employee.JoiningDate = r.JoiningDate;
        await SaveAsync(ct);
        return employee;
    }

    public Task<Employee> TerminateAsync(Guid id, TerminateRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var employee = await FindAsync(id, token);
            if (employee.Status == EmployeeStatus.Terminated)
                throw new BusinessRuleException("hr.already_terminated", "The employee is already terminated.", "تم إنهاء خدمة الموظف مسبقاً.");
            if (r.TerminationDate < employee.JoiningDate)
                throw new BusinessRuleException("hr.termination_date", "Termination date cannot be before the joining date.", "لا يمكن أن يسبق تاريخ إنهاء الخدمة تاريخ الالتحاق.");
            employee.Status = EmployeeStatus.Terminated;
            employee.TerminationDate = r.TerminationDate;

            var contract = await db.Set<EmploymentContract>().SingleOrDefaultAsync(c => c.EmployeeId == id && c.Status == ContractStatus.Active, token);
            if (contract is not null)
            {
                contract.EndDate = r.TerminationDate;
                contract.Status = ContractStatus.Ended;
            }
            return employee;
        }, ct);

    public async Task<Employee> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<Employee>().SingleOrDefaultAsync(e => e.Id == id && e.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Employee", id);

    private static void Apply(Employee e, EmployeeRequest r)
    {
        (e.NameEn, e.NameAr) = Bilingual.Require(r.NameEn, r.NameAr, "employee name");
        var nationality = Guard.Required(r.Nationality, "nationality").ToUpperInvariant();
        if (nationality.Length != 2 || !nationality.All(char.IsAsciiLetterUpper))
            throw new BusinessRuleException("hr.nationality", "Nationality must be a 2-letter ISO country code (e.g. QA, IN, PH).",
                "يجب أن تكون الجنسية رمز دولة من حرفين (مثل QA).");
        e.Nationality = nationality;
        e.Gender = r.Gender;
        e.DateOfBirth = r.DateOfBirth;
        e.DepartmentId = r.DepartmentId;
        e.BranchId = r.BranchId;
        e.CostCenterId = r.CostCenterId;
        e.JobTitleEn = r.JobTitleEn;
        e.JobTitleAr = r.JobTitleAr;
        e.Mobile = r.Mobile;
        e.Email = r.Email?.Trim().ToLowerInvariant();
        e.VisaId = string.IsNullOrWhiteSpace(r.VisaId) ? null : r.VisaId.Trim();
        e.PassportNo = string.IsNullOrWhiteSpace(r.PassportNo) ? null : r.PassportNo.Trim().ToUpperInvariant();
        e.InWps = r.InWps;
        e.SocialInsuranceScheme = r.SocialInsuranceScheme;
        e.OpeningGratuityLiability = Guard.NonNegative(r.OpeningGratuityLiability, "openingGratuityLiability");

        if (string.IsNullOrWhiteSpace(r.Qid))
        {
            e.Qid = null;
            e.QidHash = null;
        }
        else
        {
            if (!QatarIdentifiers.IsValidQid(r.Qid))
                throw new BusinessRuleException("hr.invalid_qid", "QID must be 11 digits.", "يجب أن يتكون الرقم الشخصي من 11 رقماً.");
            e.Qid = QatarIdentifiers.Normalize(r.Qid);
            e.QidHash = FieldProtector.BlindIndex(e.Qid);
        }

        if (string.IsNullOrWhiteSpace(r.Iban))
        {
            e.Iban = null;
            e.BankShortName = string.IsNullOrWhiteSpace(r.BankShortName) ? null : r.BankShortName.Trim().ToUpperInvariant();
        }
        else
        {
            if (!QatarIdentifiers.IsValidQatarIban(r.Iban))
                throw new BusinessRuleException("hr.invalid_iban", "IBAN is not a valid Qatari IBAN (QA + 27 characters).",
                    "رقم الآيبان غير صحيح (QA متبوعاً بـ 27 حرفاً).");
            e.Iban = QatarIdentifiers.Normalize(r.Iban);
            e.BankShortName = string.IsNullOrWhiteSpace(r.BankShortName)
                ? QatarIdentifiers.BankCodeFromIban(e.Iban)
                : r.BankShortName.Trim().ToUpperInvariant();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.Find(ex) is { SqlState: PostgresErrors.UniqueViolation } pg)
        {
            throw pg.ConstraintName?.Contains("qid") == true
                ? new ConflictException("hr.duplicate_qid", "Another employee already has this QID.", "يوجد موظف آخر بنفس الرقم الشخصي.")
                : new ConflictException("hr.duplicate_emp_no", "Another employee already has this employee number.", "يوجد موظف آخر بنفس الرقم الوظيفي.");
        }
    }
}

internal sealed class ContractService(ErpDbContext db, ITenantContext tenant, HrSettingsProvider settingsProvider)
{
    public Task<EmploymentContract> CreateAsync(Guid employeeId, ContractRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var companyId = tenant.RequireCompanyId();
            var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == companyId, token)
                ?? throw new NotFoundException("Employee", employeeId);
            if (r.StartDate < employee.JoiningDate)
                throw new BusinessRuleException("hr.contract_before_joining", "A contract cannot start before the joining date.", "لا يمكن أن يبدأ العقد قبل تاريخ الالتحاق.");
            if (r.Components is not { Count: > 0 })
                throw new BusinessRuleException("hr.contract_components", "A contract needs at least a basic salary.", "يجب أن يتضمن العقد الراتب الأساسي على الأقل.");

            var settings = await settingsProvider.GetAsync(token);
            var codes = r.Components.Select(c => c.ComponentCode.Trim().ToUpperInvariant()).ToList();
            var components = await db.Set<PayComponent>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && codes.Contains(c.Code)).ToDictionaryAsync(c => c.Code, token);
            var missing = codes.Except(components.Keys).ToList();
            if (missing.Count > 0)
                throw new BusinessRuleException("hr.unknown_component", $"Unknown pay component(s): {string.Join(", ", missing)}", "مكون راتب غير معروف.");
            if (components.Values.Any(c => !c.IsFixed || c.Kind != ComponentKind.Earning))
                throw new BusinessRuleException("hr.contract_component_type", "Contracts can only contain fixed earnings (basic and allowances).",
                    "يمكن أن يتضمن العقد المستحقات الثابتة فقط (الأساسي والبدلات).");

            decimal AmountOf(WpsField field) => r.Components.Where(c => components[c.ComponentCode.Trim().ToUpperInvariant()].WpsField == field).Sum(c => c.Amount);
            var basic = AmountOf(WpsField.Basic);
            if (basic < settings.MinimumBasicWage)
                throw new BusinessRuleException("hr.minimum_wage", $"Basic salary must be at least QAR {settings.MinimumBasicWage:N0} (minimum wage).",
                    $"يجب ألا يقل الراتب الأساسي عن {settings.MinimumBasicWage:N0} ريال (الحد الأدنى للأجور).");
            if (!r.AccommodationProvided && AmountOf(WpsField.Housing) < settings.MinimumHousingAllowance)
                throw new BusinessRuleException("hr.minimum_housing", $"Without employer accommodation the housing allowance must be at least QAR {settings.MinimumHousingAllowance:N0}.",
                    $"في حال عدم توفير السكن يجب ألا يقل بدل السكن عن {settings.MinimumHousingAllowance:N0} ريال.");
            if (!r.FoodProvided && AmountOf(WpsField.Food) < settings.MinimumFoodAllowance)
                throw new BusinessRuleException("hr.minimum_food", $"Without employer-provided food the food allowance must be at least QAR {settings.MinimumFoodAllowance:N0}.",
                    $"في حال عدم توفير الطعام يجب ألا يقل بدل الطعام عن {settings.MinimumFoodAllowance:N0} ريال.");
            if (r.ProbationMonths is < 0 or > 6)
                throw new BusinessRuleException("hr.probation", "Probation cannot exceed 6 months.", "لا يجوز أن تتجاوز فترة التجربة 6 أشهر.");

            var current = await db.Set<EmploymentContract>().SingleOrDefaultAsync(c => c.EmployeeId == employeeId && c.Status == ContractStatus.Active, token);
            if (current is not null)
            {
                if (r.StartDate <= current.StartDate)
                    throw new BusinessRuleException("hr.contract_overlap", "A new contract must start after the current one.", "يجب أن يبدأ العقد الجديد بعد العقد الحالي.");
                current.EndDate = r.StartDate.AddDays(-1);
                current.Status = ContractStatus.Ended;
                await db.SaveChangesAsync(token); // free the "one active contract" slot before inserting
            }

            var contract = new EmploymentContract
            {
                EmployeeId = employeeId,
                StartDate = r.StartDate,
                EndDate = r.EndDate,
                ContractType = r.ContractType,
                ProbationMonths = r.ProbationMonths,
                AccommodationProvided = r.AccommodationProvided,
                FoodProvided = r.FoodProvided,
                GratuityDaysPerYear = r.GratuityDaysPerYear,
                GratuityBasis = r.GratuityBasis,
                AnnualLeaveDays = r.AnnualLeaveDays,
                AnnualAirTicketAmount = r.AnnualAirTicketAmount is > 0 ? Money.Round(r.AnnualAirTicketAmount.Value) : null
            };
            contract.Components.AddRange(r.Components.Select(c => new ContractComponent
            {
                ContractId = contract.Id,
                PayComponentId = components[c.ComponentCode.Trim().ToUpperInvariant()].Id,
                Amount = Money.Round(Guard.NonNegative(c.Amount, "amount"))
            }));
            db.Add(contract);
            return contract;
        }, ct);
}

internal sealed class LeaveService(ErpDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<LeaveRequest> CreateAsync(LeaveRequestCreate r, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        if (r.ToDate < r.FromDate)
            throw new BusinessRuleException("hr.leave_dates", "Leave end date is before the start date.", "تاريخ نهاية الإجازة قبل تاريخ بدايتها.");
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == r.EmployeeId && e.CompanyId == companyId, ct))
            throw new NotFoundException("Employee", r.EmployeeId);
        var code = Guard.Required(r.LeaveTypeCode, "leaveTypeCode").ToUpperInvariant();
        var type = await db.Set<LeaveType>().SingleOrDefaultAsync(t => t.CompanyId == companyId && t.Code == code, ct)
            ?? throw new NotFoundException("Leave type", code);
        if (await db.Set<LeaveRequest>().AnyAsync(l => l.EmployeeId == r.EmployeeId && l.FromDate <= r.ToDate && l.ToDate >= r.FromDate
                                                     && (l.Status == LeaveStatus.Pending || l.Status == LeaveStatus.Approved), ct))
            throw new ConflictException("hr.leave_overlap", "The employee already has leave in these dates.", "لدى الموظف إجازة في هذه التواريخ.");

        var leave = new LeaveRequest
        {
            EmployeeId = r.EmployeeId,
            LeaveTypeId = type.Id,
            FromDate = r.FromDate,
            ToDate = r.ToDate,
            Days = r.ToDate.DayNumber - r.FromDate.DayNumber + 1,
            Reason = r.Reason
        };
        db.Add(leave);
        await db.SaveChangesAsync(ct);
        return leave;
    }

    public async Task<LeaveRequest> DecideAsync(Guid id, LeaveStatus decision, CancellationToken ct)
    {
        var leave = await db.Set<LeaveRequest>().SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("Leave request", id);
        var allowed = decision switch
        {
            LeaveStatus.Approved or LeaveStatus.Rejected => leave.Status == LeaveStatus.Pending,
            LeaveStatus.Cancelled => leave.Status is LeaveStatus.Pending or LeaveStatus.Approved,
            _ => false
        };
        if (!allowed)
            throw new BusinessRuleException("hr.leave_status", $"A {leave.Status} leave request cannot be changed to {decision}.", "لا يمكن تغيير حالة طلب الإجازة.");
        if (decision == LeaveStatus.Cancelled && leave.Status == LeaveStatus.Approved && await IsInPostedPayrollAsync(leave, ct))
            throw new BusinessRuleException("hr.leave_in_payroll", "This leave is already included in a posted payroll.", "هذه الإجازة مدرجة في مسيرة رواتب مرحلة.");

        leave.Status = decision;
        leave.DecidedBy = tenant.ActingUserId;
        leave.DecidedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return leave;
    }

    /// <summary>Annual leave: entitlement accrued pro rata for service to date, less approved annual leave taken.</summary>
    public async Task<object> BalanceAsync(Guid employeeId, DateOnly asOf, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == tenant.RequireCompanyId(), ct)
            ?? throw new NotFoundException("Employee", employeeId);
        var contractDays = await db.Set<EmploymentContract>().Where(c => c.EmployeeId == employeeId && c.Status == ContractStatus.Active)
            .Select(c => c.AnnualLeaveDays).FirstOrDefaultAsync(ct);
        var perYear = GratuityCalculator.AnnualLeaveDaysPerYear(employee.JoiningDate, asOf, contractDays);
        var serviceDays = GratuityCalculator.ServiceDays(employee.JoiningDate, asOf, 0);
        var accrued = Math.Round(perYear * serviceDays / 365m, 2, MidpointRounding.AwayFromZero);
        var taken = await (from l in db.Set<LeaveRequest>()
                           join t in db.Set<LeaveType>() on l.LeaveTypeId equals t.Id
                           where l.EmployeeId == employeeId && l.Status == LeaveStatus.Approved && t.DeductsAnnualBalance && l.FromDate <= asOf
                           select l.Days).SumAsync(ct);
        return new { employeeId, asOf, entitlementDaysPerYear = perYear, accrued, taken, balance = accrued - taken };
    }

    private Task<bool> IsInPostedPayrollAsync(LeaveRequest leave, CancellationToken ct) =>
        db.Set<PayrollRun>().AnyAsync(r => r.Status != PayrollStatus.Reversed && r.Status != PayrollStatus.Draft && r.Status != PayrollStatus.Calculated
                                          && r.PeriodStart <= leave.ToDate && r.PeriodEnd >= leave.FromDate, ct);
}

internal sealed class PayrollInputService(ErpDbContext db, ITenantContext tenant)
{
    public async Task<PayrollInput> CreateAsync(PayrollInputRequest r, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        if (!await db.Set<Employee>().AnyAsync(e => e.Id == r.EmployeeId && e.CompanyId == companyId, ct))
            throw new NotFoundException("Employee", r.EmployeeId);
        var code = Guard.Required(r.ComponentCode, "componentCode").ToUpperInvariant();
        var component = await db.Set<PayComponent>().SingleOrDefaultAsync(c => c.CompanyId == companyId && c.Code == code, ct)
            ?? throw new NotFoundException("Pay component", code);
        if (component.IsFixed)
            throw new BusinessRuleException("hr.input_fixed_component", $"{code} is a fixed contract component; change the contract instead.",
                $"{code} مكون ثابت في العقد؛ عدّل العقد بدلاً من ذلك.");

        var isOvertime = component.WpsField == WpsField.Overtime;
        if (isOvertime && (r.Hours is not > 0 || r.OvertimeType is null || r.Amount is not null))
            throw new BusinessRuleException("hr.input_overtime", "Overtime inputs need hours and an overtime type.", "يتطلب العمل الإضافي عدد الساعات ونوعه.");
        if (!isOvertime && (r.Amount is not > 0 || r.Hours is not null))
            throw new BusinessRuleException("hr.input_amount", "This input needs a positive amount.", "يتطلب هذا الإدخال مبلغاً موجباً.");
        await EnsurePeriodOpenAsync(r.Year, r.Month, ct);

        var input = new PayrollInput
        {
            EmployeeId = r.EmployeeId,
            Year = r.Year,
            Month = r.Month,
            PayComponentId = component.Id,
            OvertimeType = isOvertime ? r.OvertimeType : null,
            Hours = isOvertime ? r.Hours : null,
            Amount = isOvertime ? null : Money.Round(r.Amount!.Value),
            Notes = r.Notes
        };
        db.Add(input);
        await db.SaveChangesAsync(ct);
        return input;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var input = await db.Set<PayrollInput>().SingleOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Payroll input", id);
        await EnsurePeriodOpenAsync(input.Year, input.Month, ct);
        db.Remove(input);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsurePeriodOpenAsync(int year, int month, CancellationToken ct)
    {
        if (await db.Set<PayrollRun>().AnyAsync(r => r.CompanyId == tenant.RequireCompanyId() && r.Year == year && r.Month == month
                                                     && (r.Status == PayrollStatus.Approved || r.Status == PayrollStatus.Posted || r.Status == PayrollStatus.Paid), ct))
            throw new BusinessRuleException("hr.payroll_locked", $"Payroll for {year}-{month:D2} is already approved.", $"تم اعتماد رواتب {year}-{month:D2} مسبقاً.");
    }
}
