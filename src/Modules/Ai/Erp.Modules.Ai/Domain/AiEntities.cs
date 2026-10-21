using Erp.Infrastructure.Security;
using Erp.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Modules.Ai.Domain;

public enum AiProviderKind { OpenAiCompatible, Anthropic }

public enum AiMode { Platform, Own }

/// <summary>A model the platform offers every customer (no tenant, no RLS): a free cloud tier or a model on our own server.</summary>
public sealed class AiProvider : Entity
{
    public required string Code { get; init; }
    public required string Name { get; set; }
    public AiProviderKind Kind { get; set; }
    public required string BaseUrl { get; set; }
    public required string Model { get; set; }
    public string? VisionModel { get; set; }

    /// <summary>Comma-separated models tried in turn when the main one is out of its free allowance or overloaded.</summary>
    public string? FallbackModels { get; set; }

    /// <summary>Code of the provider used when this one is out of its daily allowance, busy or down (another company's free tier).</summary>
    public string? FallbackProvider { get; set; }

    [Sensitive] public string? ApiKey { get; set; }

    /// <summary>Private: what is sent is not kept or used for training (our own server, or a paid tier). Otherwise only figures and placeholders go out.</summary>
    public bool IsPrivate { get; set; }

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Requests per customer per day.</summary>
    public int DailyLimit { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>A company's AI choice: one of the platform's providers, or its own subscription (key stored encrypted).</summary>
public sealed class AiSetting : TenantEntity
{
    public Guid CompanyId { get; init; }
    public AiMode Mode { get; set; }
    public string? ProviderCode { get; set; }
    public AiProviderKind? Kind { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
    public string? VisionModel { get; set; }
    [Sensitive] public string? ApiKey { get; set; }
    public bool SummaryEnabled { get; set; } = true;
    public int SummaryHour { get; set; } = 21;
    public string SummaryLanguage { get; set; } = "ar";
    public string? SummaryRecipients { get; set; }
}

/// <summary>The owner's daily summary written by the AI from the day's figures.</summary>
public sealed class AiSummary : TenantEntity, INotAudited
{
    public Guid CompanyId { get; init; }
    public DateOnly SummaryDate { get; init; }
    public required string Language { get; init; }
    public required string Text { get; set; }
    public required string Facts { get; set; }
    public required string Provider { get; set; }
    public DateTimeOffset? EmailQueuedAt { get; set; }
    public DateTimeOffset? EmailedAt { get; set; }
}

internal sealed class AiProviderConfiguration : IEntityTypeConfiguration<AiProvider>
{
    public void Configure(EntityTypeBuilder<AiProvider> b)
    {
        b.ToTable("provider", "ai");
        b.Property(x => x.ApiKey).HasConversion(new EncryptedStringConverter());
    }
}

internal sealed class AiSettingConfiguration : IEntityTypeConfiguration<AiSetting>
{
    public void Configure(EntityTypeBuilder<AiSetting> b)
    {
        b.ToTable("setting", "ai");
        b.Property(x => x.ApiKey).HasConversion(new EncryptedStringConverter());
    }
}

internal sealed class AiSummaryConfiguration : IEntityTypeConfiguration<AiSummary>
{
    public void Configure(EntityTypeBuilder<AiSummary> b) => b.ToTable("summary", "ai");
}
