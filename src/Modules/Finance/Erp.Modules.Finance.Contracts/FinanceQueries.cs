namespace Erp.Modules.Finance.Contracts;

public sealed record AccountSummary(Guid Id, string Code, string NameEn, string NameAr, string? Subtype);

/// <summary>Read-only finance lookups other modules may need (e.g. choosing the bank account that pays salaries).</summary>
public interface IFinanceQueries
{
    Task<AccountSummary?> FindPostableAccountAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Balance (debit − credit, base currency) of the account a role resolves to, up to a date.</summary>
    Task<decimal> GetRoleBalanceAsync(string accountRole, DateOnly asOf, CancellationToken ct = default);

    /// <summary>Balance (debit − credit, base currency) of one party on the account a role resolves to, up to a date.</summary>
    Task<decimal> GetPartyBalanceAsync(string accountRole, PartyType partyType, Guid partyId, DateOnly asOf, CancellationToken ct = default);
}
