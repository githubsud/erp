using Erp.Infrastructure.Persistence;
using Erp.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Numbering;

/// <summary>
/// Gapless numbering: the sequence row is incremented with an UPDATE inside the caller's transaction, so
/// the row stays locked until commit. A rolled-back posting releases the number for the next document.
/// Format: <c>{PREFIX}-{YYYY}-{000001}</c>, one sequence per tenant, company, document type, branch and year.
/// </summary>
public sealed class NumberingService(ErpDbContext db, ITenantContext tenant) : INumberingService
{
    public async Task<string> NextAsync(string docType, string defaultPrefix, DateOnly documentDate, Guid? branchId = null,
        CancellationToken ct = default)
    {
        db.RequireTransaction("Document numbering");

        var tenantId = tenant.RequireTenantId();
        var companyId = tenant.RequireCompanyId();
        var year = documentDate.Year;
        var actor = tenant.ActingUserId;

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO core.number_sequence
                (id, tenant_id, company_id, doc_type, branch_id, fiscal_year, prefix, next_value, padding, created_by, created_at)
            VALUES ({Guid.CreateVersion7()}, {tenantId}, {companyId}, {docType}, {branchId}, {year}, {defaultPrefix}, 1, 6, {actor}, now())
            ON CONFLICT (tenant_id, company_id, doc_type, branch_id, fiscal_year) DO NOTHING
            """, ct);

        var row = await db.Database.SqlQuery<SequenceRow>($"""
            UPDATE core.number_sequence
               SET next_value = next_value + 1, updated_at = now(), updated_by = {actor}
             WHERE tenant_id = {tenantId} AND company_id = {companyId} AND doc_type = {docType}
               AND branch_id IS NOT DISTINCT FROM {branchId} AND fiscal_year = {year}
            RETURNING prefix, next_value - 1 AS value, padding
            """).ToListAsync(ct);

        var sequence = row.Single();
        return $"{sequence.Prefix}-{year}-{sequence.Value.ToString().PadLeft(sequence.Padding, '0')}";
    }

    private sealed record SequenceRow(string Prefix, long Value, int Padding);
}
