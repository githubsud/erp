namespace Erp.Modules.Finance.Contracts;

public sealed record AccountSummary(Guid Id, string Code, string NameEn, string NameAr, string? Subtype);

/// <summary>One posted line of a party on a control account (base currency).</summary>
public sealed record PartyLedgerLine(DateOnly PostingDate, string EntryNo, string SourceType, Guid SourceId, string SourceNo, string? MemoEn,
    string? MemoAr, decimal Debit, decimal Credit);

/// <summary>Net movement (debit − credit, base) of one income or expense account on lines tagged with a project.</summary>
public sealed record ProjectLedgerLine(Guid AccountId, string Code, string NameEn, string NameAr, bool IsIncome, decimal Net);

public sealed record PartyLedger(decimal OpeningBalance, IReadOnlyList<PartyLedgerLine> Lines, decimal ClosingBalance);

/// <summary>Read-only finance lookups other modules may need (e.g. choosing the bank account that pays salaries).</summary>
public interface IFinanceQueries
{
    Task<AccountSummary?> FindPostableAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Balance (debit − credit, base currency) of the account a role resolves to, up to a date.</summary>
    Task<decimal> GetRoleBalanceAsync(string accountRole, DateOnly asOf, CancellationToken ct = default);

    /// <summary>Balance (debit − credit, base currency) of one party on the account a role resolves to, up to a date.</summary>
    Task<decimal> GetPartyBalanceAsync(string accountRole, PartyType partyType, Guid partyId, DateOnly asOf, CancellationToken ct = default);

    /// <summary>A party's movements on the account a role resolves to between two dates, with opening and closing balances (debit − credit).</summary>
    /// <summary>Income and expense postings tagged with a project, by account (project profit and loss).</summary>
    Task<IReadOnlyList<ProjectLedgerLine>> GetProjectLedgerAsync(Guid projectId, CancellationToken ct = default);

    Task<PartyLedger> GetPartyLedgerAsync(string accountRole, PartyType partyType, Guid partyId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default);
}
