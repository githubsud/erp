namespace Erp.SharedKernel.Domain;

/// <summary>Base for expected, user-facing failures. Messages are bilingual.</summary>
public abstract class ErpException(string code, string message, string? messageAr) : Exception(message)
{
    public string Code { get; } = code;
    public string? MessageAr { get; } = messageAr;
    public IReadOnlyDictionary<string, object?> Details { get; init; } = new Dictionary<string, object?>();
}

/// <summary>A business rule was violated (HTTP 422).</summary>
public sealed class BusinessRuleException(string code, string message, string? messageAr = null)
    : ErpException(code, message, messageAr);

/// <summary>Entity does not exist for the current tenant (HTTP 404).</summary>
public sealed class NotFoundException : ErpException
{
    public NotFoundException(string entity, object key)
        : base("not_found", $"{entity} '{key}' was not found.", $"لم يتم العثور على {entity} '{key}'.") { }

    /// <summary>For a missing thing the user can fix, with a message that says how.</summary>
    public NotFoundException(string code, string message, string messageAr) : base(code, message, messageAr) { }
}

/// <summary>State conflict such as a duplicate code or concurrent edit (HTTP 409).</summary>
public sealed class ConflictException(string code, string message, string? messageAr = null)
    : ErpException(code, message, messageAr);

/// <summary>Caller is authenticated but not allowed (HTTP 403).</summary>
public sealed class ForbiddenException(string message = "You do not have permission for this action.")
    : ErpException("forbidden", message, "ليس لديك صلاحية لتنفيذ هذا الإجراء.");
