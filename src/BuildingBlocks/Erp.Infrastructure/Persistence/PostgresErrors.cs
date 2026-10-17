using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Infrastructure.Persistence;

public static class PostgresErrors
{
    public const string UniqueViolation = "23505";
    public const string ForeignKeyViolation = "23503";
    public const string CheckViolation = "23514";
    public const string InsufficientPrivilege = "42501";
    public const string RaiseException = "P0001";

    public static PostgresException? Find(Exception exception) => exception switch
    {
        PostgresException pg => pg,
        DbUpdateException { InnerException: PostgresException pg } => pg,
        _ => exception.InnerException is null ? null : Find(exception.InnerException)
    };
}
