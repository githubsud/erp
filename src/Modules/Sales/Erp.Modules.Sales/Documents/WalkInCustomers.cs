using Erp.Infrastructure.Persistence;
using Erp.Modules.Sales.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Sales.Documents;

/// <summary>Counter businesses (tailor, workshop) serve people who are not customers yet: they are found by phone or created as cash customers.</summary>
internal static class WalkInCustomers
{
    /// <summary>Existing customer, or a walk-in found by phone, or a new cash customer with <paramref name="codePrefix"/> + phone as its code.</summary>
    public static async Task<Customer> FindOrCreateAsync(ErpDbContext db, ITenantContext tenant, CustomerService customers, Guid? customerId, string? name,
        string? phone, string codePrefix, CancellationToken ct)
    {
        if (customerId is { } id) return await CustomerService.ActiveCustomerAsync(db, tenant, id, ct);
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 8 || string.IsNullOrWhiteSpace(name))
            throw new BusinessRuleException("sal.walkin_customer", "Choose a customer or enter the walk-in's name and phone.", "اختر عميلاً أو أدخل اسم العميل ورقم هاتفه.");
        var local = digits.Length > 8 && digits.StartsWith("974") ? digits[3..] : digits;
        var existing = await db.Set<Customer>().FirstOrDefaultAsync(c => c.CompanyId == tenant.RequireCompanyId() && c.Phone != null && c.Phone.EndsWith(local), ct);
        if (existing is not null) return existing;
        return await customers.SaveAsync(null, new CustomerRequest($"{codePrefix}{local}", name.Trim(), name.Trim(), "QA", null, null, null, local, null, null,
            PaymentTermsDays: 0), ct);
    }
}
