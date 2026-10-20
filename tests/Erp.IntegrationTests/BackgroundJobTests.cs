using Erp.Infrastructure.Email;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.HR.Documents;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class BackgroundJobTests(ErpFixture fixture)
{
    [Fact]
    public async Task Expiry_job_enqueues_milestone_reminders_and_the_dispatcher_delivers_them()
    {
        var t = await fixture.CreateTenantAsync();
        var docResponse = await t.Client.PostJsonAsync("/api/hr/company-documents",
            new { docType = "MunicipalityLicense", docNo = "BL-778", expiryDate = FinanceTests.Today.AddDays(30) });
        await docResponse.EnsureSuccessAsync();

        var job = fixture.Factory.Services.GetRequiredService<DocumentExpiryJob>();
        (await job.RunOnceAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        await using var connection = await fixture.OpenAppConnectionAsync(t.TenantId);
        const string pending = "SELECT count(*) FROM core.outbox_message WHERE type = 'hr.document_expiry_reminder' AND sent_at IS NULL";
        (await PlatformTests.CountAsync(connection, pending)).ShouldBe(1);

        await fixture.DeliverOutboxAsync(async () => await PlatformTests.CountAsync(connection, pending) == 0);
        (await PlatformTests.CountAsync(connection,
            "SELECT count(*) FROM core.outbox_message WHERE type = 'hr.document_expiry_reminder' AND sent_at IS NOT NULL AND payload->>'docNo' = 'BL-778'"))
            .ShouldBe(1);

        // Delivered as a bilingual email to everyone who manages documents (the owner here).
        var mail = fixture.Factory.Services.GetRequiredService<CapturingEmailSender>().Sent
            .Single(m => m.To.Contains($"owner@{t.Code}.qa") && m.TextBody.Contains("BL-778"));
        mail.Subject.ShouldContain("Expires in 30 days: Municipality license");
        mail.Subject.ShouldContain("ينتهي خلال 30 يوم: رخصة البلدية");
        mail.HtmlBody.ShouldContain("dir=\"rtl\"");
    }

    [Fact]
    public async Task Worker_role_can_read_only_the_outbox()
    {
        await using var connection = new NpgsqlConnection(fixture.WorkerConnectionString);
        await connection.OpenAsync();
        var read = new NpgsqlCommand("SELECT count(*) FROM fin.journal_line", connection);
        (await Should.ThrowAsync<PostgresException>(() => read.ExecuteScalarAsync())).SqlState.ShouldBe("42501");
    }
}
