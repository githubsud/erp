using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Web;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Banking;

public sealed record BankMatchDto(Guid JournalLineId, Guid JournalEntryId, string EntryNo, DateOnly PostingDate, string SourceType, string SourceNo,
    string? Memo, decimal Amount);

public sealed record BankLineDto(Guid Id, int LineNo, DateOnly Date, string? Description, string? Reference, decimal Amount, string Status,
    Guid? JournalEntryId, IReadOnlyList<BankMatchDto> Matches);

public sealed record BankStatementDto(Guid Id, string StatementNo, Guid AccountId, string AccountCode, string AccountNameEn, string AccountNameAr,
    DateOnly DateFrom, DateOnly DateTo, decimal? OpeningBalance, decimal? ClosingBalance, string? FileName, string Status, int LineCount,
    int UnmatchedCount, decimal Inflows, decimal Outflows, IReadOnlyList<BankLineDto>? Lines);

public sealed record LedgerItemDto(Guid JournalLineId, Guid JournalEntryId, string EntryNo, DateOnly PostingDate, string SourceType, string SourceNo,
    string? Memo, decimal Amount);

public sealed record BankReconciliationReport(Guid AccountId, DateOnly AsOf, DateOnly? ReconcileFrom, decimal LedgerBalance, decimal? StatementBalance,
    DateOnly? StatementDate,
    IReadOnlyList<LedgerItemDto> UnclearedLedgerItems, IReadOnlyList<BankLineDto> UnrecordedBankItems, decimal AdjustedLedgerBalance,
    decimal? AdjustedStatementBalance, decimal? Difference);

internal static class BankEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/bank-statements", (ErpDbContext db, ITenantContext t, CancellationToken ct, Guid? accountId = null) => StatementsAsync(db, t, null, accountId, ct))
            .RequirePermission(FinancePermissions.BankReconcile);
        g.MapGet("/bank-statements/template.csv", () => Results.File(BankStatementParser.Template(), "text/csv; charset=utf-8", "bank_statement_template.csv"))
            .RequirePermission(FinancePermissions.BankReconcile);
        g.MapGet("/bank-statements/{id:guid}", async (Guid id, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await StatementsAsync(db, t, id, null, ct)).SingleOrDefault() ?? throw new NotFoundException("Bank statement", id))
            .RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/import", async (IFormFile file, Guid accountId, BankReconciliationService s, CancellationToken ct,
            decimal? openingBalance = null, decimal? closingBalance = null) =>
        {
            if (file.Length > 10 * 1024 * 1024)
                throw new BusinessRuleException("import.too_large", "The file is larger than 10 MB.", "حجم الملف أكبر من 10 ميغابايت.");
            await using var stream = file.OpenReadStream();
            return await s.ImportAsync(accountId, stream, file.FileName, openingBalance, closingBalance, ct);
        }).RequirePermission(FinancePermissions.BankReconcile).DisableAntiforgery();
        g.MapPost("/bank-statements", async (BankStatementInput r, BankReconciliationService s, ErpDbContext db, ITenantContext t, CancellationToken ct) =>
            (await StatementsAsync(db, t, (await s.CreateAsync(r, null, ct)).Id, null, ct)).Single()).RequirePermission(FinancePermissions.BankReconcile);
        g.MapDelete("/bank-statements/{id:guid}", async (Guid id, BankReconciliationService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        }).RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/{id:guid}/auto-match", async (Guid id, BankReconciliationService s, CancellationToken ct) =>
            new { matched = await s.AutoMatchAsync(id, ct) }).RequirePermission(FinancePermissions.BankReconcile);

        g.MapGet("/bank-statements/lines/{lineId:guid}/candidates", CandidatesAsync).RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/lines/{lineId:guid}/match", async (Guid lineId, MatchRequest r, BankReconciliationService s, CancellationToken ct) =>
        {
            await s.MatchAsync(lineId, r, ct);
            return Results.NoContent();
        }).RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/lines/{lineId:guid}/unmatch", async (Guid lineId, BankReconciliationService s, CancellationToken ct) =>
        {
            await s.UnmatchAsync(lineId, ct);
            return Results.NoContent();
        }).RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/lines/{lineId:guid}/ignore", async (Guid lineId, BankReconciliationService s, CancellationToken ct, bool ignored = true) =>
        {
            await s.SetIgnoredAsync(lineId, ignored, ct);
            return Results.NoContent();
        }).RequirePermission(FinancePermissions.BankReconcile);
        g.MapPost("/bank-statements/lines/{lineId:guid}/post", async (Guid lineId, PostFromLineRequest r, BankReconciliationService s, CancellationToken ct) =>
        {
            await s.PostFromLineAsync(lineId, r, ct);
            return Results.NoContent();
        }).RequirePermission(FinancePermissions.BankReconcile);

        g.MapGet("/reports/bank-reconciliation", ReconciliationAsync).RequirePermission(FinancePermissions.BankReconcile);
    }

    private static async Task<List<BankStatementDto>> StatementsAsync(ErpDbContext db, ITenantContext t, Guid? id, Guid? accountId, CancellationToken ct)
    {
        var q = db.Set<BankStatement>().AsNoTracking().Include(s => s.Lines).Where(s => s.CompanyId == t.RequireCompanyId());
        if (id is not null) q = q.Where(s => s.Id == id);
        if (accountId is not null) q = q.Where(s => s.AccountId == accountId);
        var list = await q.OrderByDescending(s => s.DateTo).ThenByDescending(s => s.StatementNo).Take(200).ToListAsync(ct);
        var accountIds = list.Select(s => s.AccountId).Distinct().ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var matches = id is null ? [] : await MatchesAsync(db, list.SelectMany(s => s.Lines.Select(l => l.Id)).ToList(), ct);
        return list.Select(s => new BankStatementDto(s.Id, s.StatementNo, s.AccountId, accounts[s.AccountId].Code, accounts[s.AccountId].NameEn,
            accounts[s.AccountId].NameAr, s.DateFrom, s.DateTo, s.OpeningBalance, s.ClosingBalance, s.FileName, s.Status.ToString(), s.Lines.Count,
            s.Lines.Count(l => l.Status == BankLineStatus.Unmatched), s.Lines.Where(l => l.Amount > 0).Sum(l => l.Amount),
            -s.Lines.Where(l => l.Amount < 0).Sum(l => l.Amount),
            id is null ? null : s.Lines.OrderBy(l => l.LineNo).Select(l => LineDto(l, matches)).ToList())).ToList();
    }

    private static BankLineDto LineDto(BankStatementLine l, IReadOnlyDictionary<Guid, List<BankMatchDto>> matches) =>
        new(l.Id, l.LineNo, l.TxnDate, l.Description, l.Reference, l.Amount, l.Status.ToString(), l.JournalEntryId, matches.GetValueOrDefault(l.Id) ?? []);

    private static async Task<Dictionary<Guid, List<BankMatchDto>>> MatchesAsync(ErpDbContext db, List<Guid> lineIds, CancellationToken ct) =>
        (await (from m in db.Set<BankMatch>().AsNoTracking()
                join l in db.Set<JournalLine>() on m.JournalLineId equals l.Id
                join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                where lineIds.Contains(m.BankStatementLineId)
                select new { m.BankStatementLineId, Dto = new BankMatchDto(l.Id, e.Id, e.EntryNo, e.PostingDate, e.SourceType, e.SourceNo, e.MemoEn, m.Amount) })
            .ToListAsync(ct))
        .GroupBy(x => x.BankStatementLineId).ToDictionary(g => g.Key, g => g.Select(x => x.Dto).ToList());

    /// <summary>Uncleared ledger lines within 45 days of the statement line, closest amount first.</summary>
    private static async Task<List<LedgerItemDto>> CandidatesAsync(Guid lineId, ErpDbContext db, BankReconciliationService s, CancellationToken ct)
    {
        var line = await (from l in db.Set<BankStatementLine>().AsNoTracking()
                          join st in db.Set<BankStatement>() on l.BankStatementId equals st.Id
                          where l.Id == lineId
                          select new { l.TxnDate, l.Amount, st.AccountId }).SingleOrDefaultAsync(ct)
                   ?? throw new NotFoundException("Bank statement line", lineId);
        var candidates = await s.UnmatchedLedgerLinesAsync(line.AccountId, line.TxnDate.AddDays(-45), line.TxnDate.AddDays(45), ct);
        return await LedgerItemsAsync(db, candidates.OrderBy(c => Math.Abs(c.Amount - line.Amount))
            .ThenBy(c => Math.Abs(c.Date.DayNumber - line.TxnDate.DayNumber)).Take(50).Select(c => c.JournalLineId).ToList(), ct);
    }

    private static async Task<List<LedgerItemDto>> LedgerItemsAsync(ErpDbContext db, List<Guid> journalLineIds, CancellationToken ct)
    {
        var rows = await (from l in db.Set<JournalLine>().AsNoTracking()
                          join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                          where journalLineIds.Contains(l.Id)
                          select new LedgerItemDto(l.Id, e.Id, e.EntryNo, e.PostingDate, e.SourceType, e.SourceNo, l.Description ?? e.MemoEn,
                              l.DebitBase - l.CreditBase)).ToDictionaryAsync(x => x.JournalLineId, ct);
        return journalLineIds.Select(id => rows[id]).ToList();
    }

    /// <summary>
    /// Book side: ledger balance plus bank items not yet recorded. Bank side: latest statement closing balance plus ledger items the
    /// bank has not cleared yet (receipts in transit, cheques not presented). The two adjusted balances agree when reconciled.
    /// Ledger lines before the first imported statement are covered by its opening balance, so only later ones count as uncleared.
    /// </summary>
    private static async Task<BankReconciliationReport> ReconciliationAsync(Guid accountId, ErpDbContext db, ITenantContext t, BankReconciliationService s,
        IClock clock, CancellationToken ct, DateOnly? asOf = null)
    {
        var date = asOf ?? clock.Today;
        var companyId = t.RequireCompanyId();
        var ledger = await (from l in db.Set<JournalLine>()
                            join e in db.Set<JournalEntry>() on l.JournalEntryId equals e.Id
                            where e.CompanyId == companyId && l.AccountId == accountId && e.PostingDate <= date
                            select l.DebitBase - l.CreditBase).SumAsync(ct);
        var statement = await db.Set<BankStatement>().AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.AccountId == accountId && x.DateTo <= date && x.ClosingBalance != null)
            .OrderByDescending(x => x.DateTo).FirstOrDefaultAsync(ct);
        var reconcileFrom = await db.Set<BankStatement>().Where(x => x.CompanyId == companyId && x.AccountId == accountId)
            .MinAsync(x => (DateOnly?)x.DateFrom, ct);
        var uncleared = await s.UnmatchedLedgerLinesAsync(accountId, reconcileFrom, date, ct);
        var unrecorded = await (from l in db.Set<BankStatementLine>().AsNoTracking()
                                join st in db.Set<BankStatement>() on l.BankStatementId equals st.Id
                                where st.CompanyId == companyId && st.AccountId == accountId && l.Status == BankLineStatus.Unmatched && l.TxnDate <= date
                                orderby l.TxnDate, l.LineNo
                                select l).ToListAsync(ct);
        var items = await LedgerItemsAsync(db, uncleared.Select(c => c.JournalLineId).ToList(), ct);
        var adjustedLedger = ledger + unrecorded.Sum(l => l.Amount);
        decimal? adjustedBank = statement?.ClosingBalance is { } closing ? closing + uncleared.Sum(c => c.Amount) : null;
        return new BankReconciliationReport(accountId, date, reconcileFrom, ledger, statement?.ClosingBalance, statement?.DateTo, items,
            unrecorded.Select(l => LineDto(l, new Dictionary<Guid, List<BankMatchDto>>())).ToList(), adjustedLedger, adjustedBank,
            adjustedBank is { } b ? b - adjustedLedger : null);
    }
}
