namespace Erp.Modules.Sales.Contracts;

/// <summary>
/// One sale (or return, negative) of a partner shop's goods: the bottles, what the shopper paid and our commission, in QAR. The shop's share is
/// <see cref="Amount"/> − <see cref="Commission"/>.
/// </summary>
public sealed record ConsignmentSaleLine(DateOnly Date, string DocumentNo, Guid ItemId, decimal Quantity, decimal Amount, decimal Commission);

/// <summary>Sales of goods on trust for a partner shop's statement (posted invoices and credit notes that are not reversed).</summary>
public interface IConsignmentSales
{
    Task<IReadOnlyList<ConsignmentSaleLine>> GetLinesAsync(Guid consignorId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct = default);
}
