using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>Optimistic concurrency on PostgreSQL's system column <c>xmin</c> (no extra column needed).</summary>
    public static PropertyBuilder<uint> HasXminConcurrency(this PropertyBuilder<uint> property) =>
        property.HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

    /// <summary>Quantities and rates that need more precision than money.</summary>
    public static PropertyBuilder<decimal> AsQuantity(this PropertyBuilder<decimal> property) => property.HasPrecision(19, 6);

    public static PropertyBuilder<decimal?> AsQuantity(this PropertyBuilder<decimal?> property) => property.HasPrecision(19, 6);
}
