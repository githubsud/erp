using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Posting;

/// <summary>
/// The single writer of the general ledger. It resolves accounts from posting rules, converts to base currency,
/// balances (with a bounded rounding line), numbers the entry and inserts it — all inside the caller's
/// transaction. The database independently re-checks balance, open period and immutability.
/// </summary>
internal sealed class PostingService(
    ErpDbContext db,
    ITenantContext tenant,
    INumberingService numbering,
    FiscalCalendar calendar,
    IClock clock) : IPostingService
{
    public const string EntryDocType = "journal_entry";
    public const string EntryPrefix = "JE";

    /// <summary>Largest base-currency rounding difference absorbed automatically, per line.</summary>
    private const decimal RoundingTolerancePerLine = 0.01m;

    public async Task<PostingResult> PostAsync(PostingRequest request, CancellationToken ct = default)
    {
        db.RequireTransaction("Posting");
        Validate(request);
        var companyId = tenant.RequireCompanyId();

        var existing = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.SourceType == request.SourceType && e.SourceId == request.SourceId && e.ReversalOfId == null)
            .Select(e => new { e.Id, e.EntryNo, e.PostingDate, Total = e.Lines.Sum(l => l.DebitBase) })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new PostingResult(existing.Id, existing.EntryNo, existing.PostingDate, existing.Total);

        var period = await calendar.GetOpenPeriodAsync(request.PostingDate, ct);
        var lines = request.Lines.Where(l => l.Amount != 0).ToList();
        var accountIds = await ResolveAccountsAsync(companyId, lines, ct);
        var accounts = await db.Set<Account>().AsNoTracking()
            .Where(a => a.CompanyId == companyId && accountIds.Values.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        var built = new List<JournalLine>(lines.Count + 1);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var account = accounts.GetValueOrDefault(accountIds[i])
                ?? throw new BusinessRuleException("fin.account_not_found", "A posting account does not exist in this company.", "حساب الترحيل غير موجود في هذه الشركة.");
            ValidateAccount(account, line, request.AllowControlAccounts);
            built.Add(CreateLine(built.Count + 1, account.Id, line, request.Currency, request.FxRate));
        }

        var debitTxn = built.Sum(l => l.DebitTxn);
        var creditTxn = built.Sum(l => l.CreditTxn);
        if (debitTxn != creditTxn)
            throw new BusinessRuleException("fin.unbalanced", $"Entry is not balanced: debit {debitTxn:N2}, credit {creditTxn:N2}.",
                $"القيد غير متوازن: مدين {debitTxn:N2}، دائن {creditTxn:N2}.")
            {
                Details = new Dictionary<string, object?> { ["debit"] = debitTxn, ["credit"] = creditTxn }
            };

        var baseDifference = built.Sum(l => l.DebitBase) - built.Sum(l => l.CreditBase);
        if (baseDifference != 0)
        {
            if (Math.Abs(baseDifference) > RoundingTolerancePerLine * built.Count)
                throw new BusinessRuleException("fin.rounding_too_large", "Currency conversion produced an unexpected difference.", "نتج عن تحويل العملة فرق غير متوقع.");
            var roundingAccount = await ResolveRoleAsync(companyId, AccountRoles.Rounding, null, ct);
            built.Add(CreateLine(built.Count + 1, roundingAccount, new PostingLine
            {
                AccountRole = AccountRoles.Rounding,
                Side = baseDifference > 0 ? PostingSide.Credit : PostingSide.Debit,
                Amount = Math.Abs(baseDifference),
                Description = "Rounding difference"
            }, Money.BaseCurrency, 1m));
        }

        // Number only after every check has passed, so a rejected posting never consumes a number.
        var entryNo = await numbering.NextAsync(EntryDocType, EntryPrefix, request.PostingDate, ct: ct);
        var entry = new JournalEntry
        {
            CompanyId = companyId,
            BranchId = request.BranchId,
            EntryNo = entryNo,
            PostingDate = request.PostingDate,
            FiscalPeriodId = period.Id,
            SourceType = request.SourceType,
            SourceId = request.SourceId,
            SourceNo = request.SourceNo,
            MemoEn = Truncate(request.MemoEn),
            MemoAr = Truncate(request.MemoAr),
            PostedBy = tenant.ActingUserId,
            PostedAt = clock.UtcNow
        };
        entry.Lines.AddRange(built);
        db.Add(entry);
        await db.SaveChangesAsync(ct);

        return new PostingResult(entry.Id, entryNo, entry.PostingDate, built.Sum(l => l.DebitBase));
    }

    public async Task<PostingResult> ReverseAsync(string sourceType, Guid sourceId, DateOnly reversalDate, string reason, CancellationToken ct = default)
    {
        db.RequireTransaction("Reversal");
        reason = Guard.Required(reason, "reason");

        var original = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .SingleOrDefaultAsync(e => e.SourceType == sourceType && e.SourceId == sourceId && e.ReversalOfId == null, ct)
            ?? throw new NotFoundException("Journal entry for source", $"{sourceType}/{sourceId}");

        if (await db.Set<JournalEntry>().AnyAsync(e => e.ReversalOfId == original.Id, ct))
            throw new BusinessRuleException("fin.already_reversed", $"Entry {original.EntryNo} has already been reversed.", $"تم عكس القيد {original.EntryNo} مسبقاً.");
        if (reversalDate < original.PostingDate)
            throw new BusinessRuleException("fin.reversal_before_original", "A reversal cannot be dated before the original entry.", "لا يمكن أن يسبق تاريخ العكس تاريخ القيد الأصلي.");

        var period = await calendar.GetOpenPeriodAsync(reversalDate, ct);
        var entryNo = await numbering.NextAsync(EntryDocType, EntryPrefix, reversalDate, ct: ct);
        var reversal = new JournalEntry
        {
            CompanyId = original.CompanyId,
            BranchId = original.BranchId,
            EntryNo = entryNo,
            PostingDate = reversalDate,
            FiscalPeriodId = period.Id,
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            SourceNo = original.SourceNo,
            ReversalOfId = original.Id,
            MemoEn = Truncate($"Reversal of {original.EntryNo}: {reason}"),
            MemoAr = Truncate($"عكس القيد {original.EntryNo}: {reason}"),
            PostedBy = tenant.ActingUserId,
            PostedAt = clock.UtcNow
        };
        reversal.Lines.AddRange(original.Lines.OrderBy(l => l.LineNo).Select(l => new JournalLine
        {
            LineNo = l.LineNo,
            AccountId = l.AccountId,
            PartyType = l.PartyType,
            PartyId = l.PartyId,
            CostCenterId = l.CostCenterId,
            ProjectId = l.ProjectId,
            Currency = l.Currency,
            FxRate = l.FxRate,
            DebitTxn = l.CreditTxn,
            CreditTxn = l.DebitTxn,
            DebitBase = l.CreditBase,
            CreditBase = l.DebitBase,
            Description = l.Description
        }));

        db.Add(reversal);
        await db.SaveChangesAsync(ct);
        return new PostingResult(reversal.Id, entryNo, reversalDate, reversal.Lines.Sum(l => l.DebitBase));
    }

    private static void Validate(PostingRequest request)
    {
        Guard.Required(request.SourceType, "sourceType");
        Guard.NotEmpty(request.SourceId, "sourceId");
        if (request.FxRate <= 0)
            throw new BusinessRuleException("fin.invalid_fx_rate", "Exchange rate must be positive.", "يجب أن يكون سعر الصرف موجباً.");
        if (request.Currency is not { Length: 3 })
            throw new BusinessRuleException("fin.invalid_currency", "Currency must be a 3-letter ISO code.", "يجب أن يكون رمز العملة من 3 أحرف.");
        if (request.Currency == Money.BaseCurrency && request.FxRate != 1m)
            throw new BusinessRuleException("fin.invalid_fx_rate", "Base currency postings must use an exchange rate of 1.", "يجب أن يكون سعر الصرف 1 للعملة الأساسية.");

        foreach (var line in request.Lines)
        {
            if (line.Amount < 0)
                throw new BusinessRuleException("fin.negative_amount", "Posting amounts must be positive; use the opposite side instead.", "يجب أن تكون مبالغ الترحيل موجبة.");
            if (line.AccountId is null && string.IsNullOrWhiteSpace(line.AccountRole))
                throw new BusinessRuleException("fin.account_required", "Each line needs an account or an account role.", "يجب تحديد حساب أو دور محاسبي لكل سطر.");
            if ((line.PartyType is null) != (line.PartyId is null))
                throw new BusinessRuleException("fin.party_incomplete", "Party type and party id must be given together.", "يجب تحديد نوع الطرف ورقمه معاً.");
        }

        var nonZero = request.Lines.Where(l => l.Amount != 0).ToList();
        if (!nonZero.Any(l => l.Side == PostingSide.Debit) || !nonZero.Any(l => l.Side == PostingSide.Credit))
            throw new BusinessRuleException("fin.one_sided", "An entry needs at least one debit and one credit.", "يجب أن يحتوي القيد على طرف مدين وطرف دائن على الأقل.");
    }

    private static void ValidateAccount(Account account, PostingLine line, bool allowControl)
    {
        if (!account.IsPostable)
            throw new BusinessRuleException("fin.account_not_postable", $"Account {account.Code} is a group account and cannot be posted to.",
                $"الحساب {account.Code} حساب تجميعي ولا يقبل الترحيل.");
        if (!account.IsActive)
            throw new BusinessRuleException("fin.account_inactive", $"Account {account.Code} is inactive.", $"الحساب {account.Code} غير نشط.");
        if (account.IsControl)
        {
            if (!allowControl)
                throw new BusinessRuleException("fin.control_account_manual",
                    $"Account {account.Code} is a control account; post through its sub-ledger document instead.",
                    $"الحساب {account.Code} حساب رقابي؛ استخدم مستند الدفتر المساعد.");
            if (line.PartyId is null)
                throw new BusinessRuleException("fin.party_required", $"Account {account.Code} requires a party.", $"الحساب {account.Code} يتطلب تحديد طرف.");
        }
    }

    private async Task<Dictionary<int, Guid>> ResolveAccountsAsync(Guid companyId, IReadOnlyList<PostingLine> lines, CancellationToken ct)
    {
        var roles = lines.Where(l => l.AccountId is null).Select(l => l.AccountRole!).Distinct().ToList();
        var rules = roles.Count == 0
            ? []
            : await db.Set<PostingRule>().AsNoTracking()
                .Where(r => r.CompanyId == companyId && roles.Contains(r.AccountRole))
                .ToListAsync(ct);

        var result = new Dictionary<int, Guid>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            result[i] = line.AccountId ?? Resolve(rules, line.AccountRole!, line.RoleKey);
        }
        return result;
    }

    private async Task<Guid> ResolveRoleAsync(Guid companyId, string role, Guid? key, CancellationToken ct)
    {
        var rules = await db.Set<PostingRule>().AsNoTracking().Where(r => r.CompanyId == companyId && r.AccountRole == role).ToListAsync(ct);
        return Resolve(rules, role, key);
    }

    /// <summary>Most specific rule wins: (role, key) then (role, no key).</summary>
    internal static Guid Resolve(IReadOnlyList<PostingRule> rules, string role, Guid? key) =>
        (key is null ? null : rules.FirstOrDefault(r => r.AccountRole == role && r.RoleKey == key))?.AccountId
        ?? rules.FirstOrDefault(r => r.AccountRole == role && r.RoleKey is null)?.AccountId
        ?? throw new BusinessRuleException("fin.posting_rule_missing", $"No account is mapped to posting role '{role}'.",
            $"لا يوجد حساب مرتبط بدور الترحيل '{role}'.") { Details = new Dictionary<string, object?> { ["role"] = role } };

    private static JournalLine CreateLine(int lineNo, Guid accountId, PostingLine line, string currency, decimal fxRate)
    {
        var txn = Money.Round(line.Amount);
        var baseAmount = Money.Round(txn * fxRate);
        var debit = line.Side == PostingSide.Debit;
        return new JournalLine
        {
            LineNo = lineNo,
            AccountId = accountId,
            PartyType = line.PartyType,
            PartyId = line.PartyId,
            CostCenterId = line.CostCenterId,
            ProjectId = line.ProjectId,
            Currency = currency,
            FxRate = fxRate,
            DebitTxn = debit ? txn : 0,
            CreditTxn = debit ? 0 : txn,
            DebitBase = debit ? baseAmount : 0,
            CreditBase = debit ? 0 : baseAmount,
            Description = Truncate(line.Description)
        };
    }

    private static string? Truncate(string? value) => value is { Length: > 500 } ? value[..500] : value;
}
