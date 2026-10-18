using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Banking;

public sealed record BankCandidate(Guid JournalLineId, DateOnly Date, decimal Amount, string Text);

public sealed record StatementLineToMatch(Guid LineId, DateOnly Date, decimal Amount, string? Description, string? Reference);

/// <summary>
/// Proposes one-to-one matches between statement lines and ledger lines on the same bank account: the amount must be equal
/// and the dates at most <see cref="DayWindow"/> days apart. Among several candidates a reference found in the ledger text
/// wins, then the closest date; anything still ambiguous is left for the user.
/// </summary>
public static class BankMatcher
{
    public const int DayWindow = 7;

    public static List<(Guid LineId, Guid JournalLineId)> Propose(IEnumerable<StatementLineToMatch> lines, IEnumerable<BankCandidate> candidates)
    {
        var pool = candidates.ToList();
        var result = new List<(Guid, Guid)>();
        foreach (var line in lines.OrderBy(l => l.Date))
        {
            var options = pool.Where(c => c.Amount == line.Amount && Math.Abs(c.Date.DayNumber - line.Date.DayNumber) <= DayWindow).ToList();
            if (options.Count == 0) continue;
            var reference = Token(line.Reference);
            List<BankCandidate> byReference = reference is null ? [] : options.Where(c => c.Text.Contains(reference, StringComparison.OrdinalIgnoreCase)).ToList();
            var ranked = (byReference.Count > 0 ? byReference : options)
                .GroupBy(c => Math.Abs(c.Date.DayNumber - line.Date.DayNumber)).OrderBy(g => g.Key).First().ToList();
            if (ranked.Count != 1) continue;
            result.Add((line.LineId, ranked[0].JournalLineId));
            pool.Remove(ranked[0]);
        }
        return result;
    }

    /// <summary>References shorter than 4 characters match too much text to be useful.</summary>
    private static string? Token(string? reference) => reference?.Trim() is { Length: >= 4 } r ? r : null;
}

public sealed record BankStatementLineInput(DateOnly Date, string? Description, string? Reference, decimal Amount);

public sealed record BankStatementInput(Guid AccountId, DateOnly? DateFrom, DateOnly? DateTo, decimal? OpeningBalance, decimal? ClosingBalance,
    IReadOnlyList<BankStatementLineInput> Lines);

public sealed record MatchRequest(IReadOnlyList<Guid> JournalLineIds);

public sealed record PostFromLineRequest(Guid AccountId, string? Description, Guid? CostCenterId);

public sealed record ImportResult(Guid StatementId, int Lines, int AutoMatched, IReadOnlyList<string> Errors);

/// <summary>Imports statements, matches them to the ledger (automatically and by hand) and posts bank-only items.</summary>
internal sealed class BankReconciliationService(ErpDbContext db, ITenantContext tenant, INumberingService numbering, IPostingService posting)
{
    public const string SourceType = "bank_statement_line";

    public async Task<ImportResult> ImportAsync(Guid accountId, Stream file, string fileName, decimal? openingBalance, decimal? closingBalance,
        CancellationToken ct)
    {
        ParsedStatement parsed;
        try
        {
            parsed = BankStatementParser.Read(file, fileName);
        }
        catch (Exception ex) when (ex is not BusinessRuleException)
        {
            throw new BusinessRuleException("import.unreadable", "The file could not be read. Use .xlsx or .csv.", "تعذر قراءة الملف. استخدم ملف xlsx أو csv.");
        }
        if (parsed.Lines.Count == 0)
            throw new BusinessRuleException("bank.empty", "The statement has no transactions.", "لا يحتوي الكشف على حركات.");
        var last = parsed.Lines.MaxBy(l => (l.Date, l.Row))!;
        var input = new BankStatementInput(accountId, null, null, openingBalance, closingBalance ?? last.Balance,
            parsed.Lines.Select(l => new BankStatementLineInput(l.Date, l.Description, l.Reference, l.Amount)).ToList());
        var statement = await CreateAsync(input, fileName, ct);
        return new ImportResult(statement.Id, statement.Lines.Count, statement.Lines.Count(l => l.Status == BankLineStatus.Matched), parsed.Errors);
    }

    public Task<BankStatement> CreateAsync(BankStatementInput r, string? fileName, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            await BankAccountAsync(r.AccountId, token);
            if (r.Lines is not { Count: > 0 } || r.Lines.Any(l => l.Amount == 0))
                throw new BusinessRuleException("bank.lines", "Enter the statement transactions (non-zero amounts).", "أدخل حركات الكشف بمبالغ غير صفرية.");
            var from = r.DateFrom ?? r.Lines.Min(l => l.Date);
            var to = r.DateTo ?? r.Lines.Max(l => l.Date);
            var statement = new BankStatement
            {
                CompanyId = tenant.RequireCompanyId(),
                AccountId = r.AccountId,
                StatementNo = await numbering.NextAsync("bank_statement", "BST", to, ct: token),
                DateFrom = from,
                DateTo = to,
                OpeningBalance = r.OpeningBalance,
                ClosingBalance = r.ClosingBalance,
                FileName = fileName
            };
            var lineNo = 0;
            foreach (var l in r.Lines.OrderBy(l => l.Date))
                statement.Lines.Add(new BankStatementLine
                {
                    BankStatementId = statement.Id, LineNo = ++lineNo, TxnDate = l.Date, Description = Clean(l.Description), Reference = Clean(l.Reference),
                    Amount = Money.Round(l.Amount)
                });
            db.Add(statement);
            await db.SaveChangesAsync(token);
            await AutoMatchCoreAsync(statement, token);
            await db.SaveChangesAsync(token);
            return statement;
        }, ct);

    public Task<int> AutoMatchAsync(Guid statementId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var statement = await FindAsync(statementId, token);
            var matched = await AutoMatchCoreAsync(statement, token);
            await db.SaveChangesAsync(token);
            return matched;
        }, ct);

    public Task MatchAsync(Guid lineId, MatchRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (statement, line) = await LineAsync(lineId, token);
            if (line.Status != BankLineStatus.Unmatched)
                throw new ConflictException("bank.line_not_open", "This statement line is already matched or ignored.", "سطر الكشف مطابق أو متجاهل مسبقاً.");
            if (r.JournalLineIds is not { Count: > 0 } || r.JournalLineIds.Distinct().Count() != r.JournalLineIds.Count)
                throw new BusinessRuleException("bank.match_lines", "Choose the ledger lines that make up this transaction.", "اختر قيود الدفتر المكونة لهذه الحركة.");
            var candidates = await UnmatchedLedgerLinesAsync(statement.AccountId, null, null, token);
            var chosen = candidates.Where(c => r.JournalLineIds.Contains(c.JournalLineId)).ToList();
            if (chosen.Count != r.JournalLineIds.Count)
                throw new BusinessRuleException("bank.match_unavailable", "Some ledger lines are not on this bank account or are already matched.",
                    "بعض القيود ليست على هذا الحساب البنكي أو تمت مطابقتها مسبقاً.");
            if (chosen.Sum(c => c.Amount) != line.Amount)
                throw new BusinessRuleException("bank.match_amount", $"The ledger lines total {chosen.Sum(c => c.Amount):N2}, the statement line is {line.Amount:N2}.",
                    $"مجموع القيود {chosen.Sum(c => c.Amount):N2} ومبلغ سطر الكشف {line.Amount:N2}.");
            foreach (var c in chosen) db.Add(new BankMatch { BankStatementLineId = line.Id, JournalLineId = c.JournalLineId, Amount = c.Amount });
            line.Status = BankLineStatus.Matched;
            RefreshStatus(statement);
            await db.SaveChangesAsync(token);
        }, ct);

    public Task UnmatchAsync(Guid lineId, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (statement, line) = await LineAsync(lineId, token);
            if (line.JournalEntryId is not null)
                throw new BusinessRuleException("bank.line_posted", "This line was posted from the statement; reverse that journal entry instead.",
                    "تم ترحيل هذا السطر من الكشف؛ قم بعكس القيد بدلاً من ذلك.");
            db.RemoveRange(await db.Set<BankMatch>().Where(m => m.BankStatementLineId == line.Id).ToListAsync(token));
            line.Status = BankLineStatus.Unmatched;
            RefreshStatus(statement);
            await db.SaveChangesAsync(token);
        }, ct);

    public Task SetIgnoredAsync(Guid lineId, bool ignored, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (statement, line) = await LineAsync(lineId, token);
            if (line.Status == BankLineStatus.Matched)
                throw new ConflictException("bank.line_matched", "Unmatch the line first.", "ألغِ المطابقة أولاً.");
            line.Status = ignored ? BankLineStatus.Ignored : BankLineStatus.Unmatched;
            RefreshStatus(statement);
            await db.SaveChangesAsync(token);
        }, ct);

    /// <summary>
    /// Posts a transaction that so far exists only at the bank (charges, interest, a transfer not yet recorded) against the
    /// chosen account and matches the new bank ledger line to the statement line.
    /// </summary>
    public Task PostFromLineAsync(Guid lineId, PostFromLineRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var (statement, line) = await LineAsync(lineId, token);
            if (line.Status != BankLineStatus.Unmatched)
                throw new ConflictException("bank.line_not_open", "This statement line is already matched or ignored.", "سطر الكشف مطابق أو متجاهل مسبقاً.");
            if (r.AccountId == statement.AccountId)
                throw new BusinessRuleException("bank.same_account", "Choose the other side of the entry, not the bank account itself.",
                    "اختر الطرف المقابل للقيد وليس الحساب البنكي نفسه.");
            var description = Clean(r.Description) ?? line.Description ?? "Bank statement item";
            var inflow = line.Amount > 0;
            var amount = Math.Abs(line.Amount);
            var result = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType, SourceId = line.Id, SourceNo = $"{statement.StatementNo}/{line.LineNo}", PostingDate = line.TxnDate,
                MemoEn = description, MemoAr = description, AllowControlAccounts = false,
                Lines =
                [
                    new PostingLine { AccountId = statement.AccountId, Side = inflow ? PostingSide.Debit : PostingSide.Credit, Amount = amount, Description = description },
                    new PostingLine
                    {
                        AccountId = r.AccountId, Side = inflow ? PostingSide.Credit : PostingSide.Debit, Amount = amount, CostCenterId = r.CostCenterId,
                        Description = description
                    }
                ]
            }, token);
            var bankLine = await db.Set<JournalLine>().AsNoTracking()
                .SingleAsync(l => l.JournalEntryId == result.JournalEntryId && l.AccountId == statement.AccountId, token);
            db.Add(new BankMatch { BankStatementLineId = line.Id, JournalLineId = bankLine.Id, Amount = line.Amount });
            line.Status = BankLineStatus.Matched;
            line.JournalEntryId = result.JournalEntryId;
            RefreshStatus(statement);
            await db.SaveChangesAsync(token);
        }, ct);

    public async Task DeleteAsync(Guid statementId, CancellationToken ct)
    {
        var statement = await FindAsync(statementId, ct);
        if (statement.Lines.Any(l => l.JournalEntryId is not null))
            throw new BusinessRuleException("bank.statement_posted", "Entries were posted from this statement; it cannot be deleted.",
                "تم ترحيل قيود من هذا الكشف ولا يمكن حذفه.");
        db.Remove(statement);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Ledger lines on a bank account not yet cleared by any statement line (signed: receipts positive).</summary>
    public async Task<List<BankCandidate>> UnmatchedLedgerLinesAsync(Guid accountId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var companyId = tenant.RequireCompanyId();
        var q = from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                where e.CompanyId == companyId && l.AccountId == accountId && !db.Set<BankMatch>().Any(m => m.JournalLineId == l.Id)
                select new { l.Id, e.PostingDate, Net = l.DebitBase - l.CreditBase, e.SourceNo, e.MemoEn, l.Description };
        if (from is { } f) q = q.Where(x => x.PostingDate >= f);
        if (to is { } t) q = q.Where(x => x.PostingDate <= t);
        return (await q.OrderBy(x => x.PostingDate).ToListAsync(ct))
            .Select(x => new BankCandidate(x.Id, x.PostingDate, x.Net, $"{x.SourceNo} {x.MemoEn} {x.Description}")).ToList();
    }

    private async Task<int> AutoMatchCoreAsync(BankStatement statement, CancellationToken ct)
    {
        var open = statement.Lines.Where(l => l.Status == BankLineStatus.Unmatched).ToList();
        if (open.Count == 0) return 0;
        var candidates = await UnmatchedLedgerLinesAsync(statement.AccountId, open.Min(l => l.TxnDate).AddDays(-BankMatcher.DayWindow),
            open.Max(l => l.TxnDate).AddDays(BankMatcher.DayWindow), ct);
        var proposals = BankMatcher.Propose(open.Select(l => new StatementLineToMatch(l.Id, l.TxnDate, l.Amount, l.Description, l.Reference)), candidates);
        foreach (var (lineId, journalLineId) in proposals)
        {
            var line = open.Single(l => l.Id == lineId);
            db.Add(new BankMatch { BankStatementLineId = lineId, JournalLineId = journalLineId, Amount = line.Amount });
            line.Status = BankLineStatus.Matched;
        }
        RefreshStatus(statement);
        return proposals.Count;
    }

    private static void RefreshStatus(BankStatement statement) =>
        statement.Status = statement.Lines.All(l => l.Status != BankLineStatus.Unmatched) ? BankStatementStatus.Reconciled : BankStatementStatus.Open;

    private async Task BankAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.Set<Account>().AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == tenant.RequireCompanyId(), ct)
                      ?? throw new NotFoundException("Account", accountId);
        if (account.Subtype != "bank" || !account.IsPostable)
            throw new BusinessRuleException("bank.not_bank_account", "Choose a bank account.", "اختر حساباً بنكياً.");
    }

    private async Task<BankStatement> FindAsync(Guid id, CancellationToken ct) =>
        await db.Set<BankStatement>().Include(s => s.Lines).SingleOrDefaultAsync(s => s.Id == id && s.CompanyId == tenant.RequireCompanyId(), ct)
        ?? throw new NotFoundException("Bank statement", id);

    private async Task<(BankStatement Statement, BankStatementLine Line)> LineAsync(Guid lineId, CancellationToken ct)
    {
        var statementId = await db.Set<BankStatementLine>().Where(l => l.Id == lineId).Select(l => (Guid?)l.BankStatementId).SingleOrDefaultAsync(ct)
                          ?? throw new NotFoundException("Bank statement line", lineId);
        var statement = await FindAsync(statementId, ct);
        return (statement, statement.Lines.Single(l => l.Id == lineId));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
