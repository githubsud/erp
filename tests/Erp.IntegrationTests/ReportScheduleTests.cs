using System.Net;
using System.Text;
using System.Text.Json;
using Erp.Infrastructure.Email;
using Erp.Infrastructure.Outbox;
using Erp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class ReportScheduleTests(ErpFixture fixture)
{
    [Fact]
    public async Task A_scheduled_report_is_emailed_as_csv_to_its_recipients()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await InventoryTests.CreateItemAsync(c, "LOW-1", reorder: 5m);

        var available = await c.GetAsync<List<JsonElement>>("/api/core/report-schedules/reports");
        available.Select(r => r.GetProperty("key").GetString()).ShouldContain("inv.reorder");
        available.Select(r => r.GetProperty("key").GetString()).ShouldContain("fin.trial_balance");

        (await (await c.PostJsonAsync("/api/core/report-schedules", new { reportKey = "inv.reorder", frequency = "Weekly", recipients = new[] { "store@t.qa" } }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("core.schedule_weekday");
        (await (await c.PostJsonAsync("/api/core/report-schedules", new { reportKey = "inv.reorder", frequency = "Daily", recipients = new[] { "not-an-email" } }))
            .ProblemAsync()).GetProperty("code").GetString().ShouldBe("core.schedule_email");

        var mailbox = $"store-{Guid.NewGuid():N}@t.qa";
        var id = await c.PostAsync<Guid>("/api/core/report-schedules", new
        {
            reportKey = "inv.reorder", frequency = "Monthly", monthDay = 1, recipients = new[] { mailbox, "gm@t.qa" }
        });
        var schedule = (await c.GetAsync<List<JsonElement>>("/api/core/report-schedules")).Single(s => s.GetProperty("id").GetGuid() == id);
        DateOnly.Parse(schedule.GetProperty("nextRunOn").GetString()!).Day.ShouldBe(1);

        (await c.PostJsonAsync($"/api/core/report-schedules/{id}/send-now", new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var dispatcher = fixture.Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(CancellationToken.None) > 0) { }

        var mail = fixture.Factory.Services.GetRequiredService<CapturingEmailSender>().Sent.Single(m => m.To.Contains(mailbox));
        mail.To.ShouldContain("gm@t.qa");
        mail.Subject.ShouldContain("Items to reorder");
        var file = mail.Attachments.ShouldNotBeNull().ShouldHaveSingleItem();
        file.ContentType.ShouldBe("text/csv");
        var csv = Encoding.UTF8.GetString(file.Content);
        csv.ShouldContain("LOW-1");
        csv.ShouldContain("الصنف");
        (await c.GetAsync<List<JsonElement>>("/api/core/report-schedules")).Single().GetProperty("lastSentAt").ValueKind.ShouldNotBe(JsonValueKind.Null);

        (await c.DeleteAsync($"/api/core/report-schedules/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
