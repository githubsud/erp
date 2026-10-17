namespace Erp.SharedKernel.Domain;

public static class Bilingual
{
    /// <summary>
    /// Master data needs both an English and an Arabic name. If only one is supplied it is copied to the
    /// other so the record is usable immediately; the UI flags such records for translation review.
    /// </summary>
    public static (string En, string Ar) Require(string? en, string? ar, string field = "name")
    {
        en = string.IsNullOrWhiteSpace(en) ? null : en.Trim();
        ar = string.IsNullOrWhiteSpace(ar) ? null : ar.Trim();
        if (en is null && ar is null)
            throw new BusinessRuleException("validation.bilingual_required",
                $"An English or Arabic {field} is required.", "يجب إدخال الاسم بالعربية أو بالإنجليزية.");
        return (en ?? ar!, ar ?? en!);
    }
}

public static class Guard
{
    public static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessRuleException("validation.required", $"'{field}' is required.", $"الحقل '{field}' مطلوب.");
        return value.Trim();
    }

    public static decimal NonNegative(decimal value, string field)
    {
        if (value < 0)
            throw new BusinessRuleException("validation.negative", $"'{field}' cannot be negative.", $"لا يمكن أن تكون قيمة '{field}' سالبة.");
        return value;
    }

    public static Guid NotEmpty(Guid value, string field)
    {
        if (value == Guid.Empty)
            throw new BusinessRuleException("validation.required", $"'{field}' is required.", $"الحقل '{field}' مطلوب.");
        return value;
    }
}
