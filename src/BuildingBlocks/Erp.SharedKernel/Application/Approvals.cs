namespace Erp.SharedKernel.Application;

/// <summary>A document asking for approval. <see cref="Amount"/> (in QAR) selects which approval steps apply.</summary>
public sealed record ApprovalSubmission(string DocType, Guid DocId, string DocNo, decimal Amount, string SummaryEn, string SummaryAr);

public enum ApprovalOutcome { Approved, Pending }

/// <summary>One approval step of a rule: documents of at least <see cref="MinAmount"/> need a user with <see cref="RoleCode"/>.</summary>
public sealed record ApprovalStepRule(decimal MinAmount, string RoleCode);

/// <summary>
/// Amount-based approval workflow shared by all modules (Core implements it). Modules submit documents inside their own
/// transaction; when the last step is approved (or a step is rejected) Core calls the module's <see cref="IApprovalDocument"/>
/// in the approver's transaction.
/// </summary>
public interface IApprovalService
{
    /// <summary>Starts approval. Returns <see cref="ApprovalOutcome.Approved"/> straight away when no step applies to the amount.</summary>
    Task<ApprovalOutcome> SubmitAsync(ApprovalSubmission submission, CancellationToken ct = default);

    /// <summary>Approves or rejects the current step of the document's pending request on behalf of the signed-in user.</summary>
    Task DecideAsync(string docType, Guid docId, bool approve, string? comment, CancellationToken ct = default);

    /// <summary>Withdraws a pending request (the document was cancelled or sent back to draft by its owner).</summary>
    Task WithdrawAsync(string docType, Guid docId, CancellationToken ct = default);
}

/// <summary>A module's document type that can go through approval.</summary>
public interface IApprovalDocument
{
    string DocType { get; }
    string NameEn { get; }
    string NameAr { get; }

    /// <summary>Rules created for new companies.</summary>
    IReadOnlyList<ApprovalStepRule> DefaultSteps { get; }

    Task OnApprovedAsync(Guid docId, CancellationToken ct);
    Task OnRejectedAsync(Guid docId, string? comment, CancellationToken ct);
}
