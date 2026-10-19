using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Contracts;
using Erp.Modules.Finance.Domain;
using Erp.SharedKernel.Application;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Finance.Vouchers;

public sealed record JournalVoucherLineRequest(Guid AccountId, decimal Debit, decimal Credit, Guid? CostCenterId, string? Description, Guid? ProjectId = null);

public sealed record CreateJournalVoucherRequest(DateOnly Date, string? MemoEn, string? MemoAr, Guid? BranchId,
    IReadOnlyList<JournalVoucherLineRequest> Lines);

public sealed record ReverseRequest(DateOnly Date, string Reason);

public sealed record JournalVoucherDto(Guid Id, string VoucherNo, DateOnly VoucherDate, string? MemoEn, string? MemoAr,
    string Status, Guid JournalEntryId, Guid? ReversalEntryId);

internal sealed class JournalVoucherService(ErpDbContext db, ITenantContext tenant, IPostingService posting, INumberingService numbering)
{
    public const string SourceType = "journal_voucher";

    public Task<JournalVoucherDto> CreateAndPostAsync(CreateJournalVoucherRequest request, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            if (request.Lines is not { Count: >= 2 })
                throw new BusinessRuleException("fin.voucher_lines", "A journal voucher needs at least two lines.", "يحتاج قيد اليومية إلى سطرين على الأقل.");
            foreach (var l in request.Lines)
                if (l.Debit < 0 || l.Credit < 0 || (l.Debit == 0) == (l.Credit == 0))
                    throw new BusinessRuleException("fin.voucher_line_side", "Each line must have either a debit or a credit amount.",
                        "يجب أن يحتوي كل سطر على مبلغ مدين أو دائن فقط.");

            var voucherNo = await numbering.NextAsync(SourceType, "JV", request.Date, request.BranchId, token);
            var voucher = new JournalVoucher
            {
                CompanyId = tenant.RequireCompanyId(),
                BranchId = request.BranchId,
                VoucherNo = voucherNo,
                VoucherDate = request.Date,
                MemoEn = request.MemoEn,
                MemoAr = request.MemoAr
            };

            var result = await posting.PostAsync(new PostingRequest
            {
                SourceType = SourceType,
                SourceId = voucher.Id,
                SourceNo = voucherNo,
                PostingDate = request.Date,
                BranchId = request.BranchId,
                MemoEn = request.MemoEn,
                MemoAr = request.MemoAr,
                AllowControlAccounts = false,
                Lines = request.Lines.Select(l => new PostingLine
                {
                    AccountId = l.AccountId,
                    Side = l.Debit > 0 ? PostingSide.Debit : PostingSide.Credit,
                    Amount = l.Debit > 0 ? l.Debit : l.Credit,
                    CostCenterId = l.CostCenterId,
                    ProjectId = l.ProjectId,
                    Description = l.Description
                }).ToList()
            }, token);

            voucher.JournalEntryId = result.JournalEntryId;
            db.Add(voucher);
            return ToDto(voucher);
        }, ct);

    public Task<JournalVoucherDto> ReverseAsync(Guid id, ReverseRequest request, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var voucher = await db.Set<JournalVoucher>().SingleOrDefaultAsync(v => v.Id == id, token)
                ?? throw new NotFoundException("Journal voucher", id);
            if (voucher.Status == VoucherStatus.Reversed)
                throw new BusinessRuleException("fin.already_reversed", "This voucher has already been reversed.", "تم عكس هذا القيد مسبقاً.");

            var result = await posting.ReverseAsync(SourceType, voucher.Id, request.Date, request.Reason, token);
            voucher.Status = VoucherStatus.Reversed;
            voucher.ReversalEntryId = result.JournalEntryId;
            return ToDto(voucher);
        }, ct);

    public static JournalVoucherDto ToDto(JournalVoucher v) =>
        new(v.Id, v.VoucherNo, v.VoucherDate, v.MemoEn, v.MemoAr, v.Status.ToString(), v.JournalEntryId, v.ReversalEntryId);
}
