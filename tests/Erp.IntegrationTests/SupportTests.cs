using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Infrastructure.Email;
using Erp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class SupportTests(ErpFixture fixture)
{
    public sealed record Row(Guid Id, string TicketNo, Guid TenantId, string? CustomerCode, string Type, string Priority, string Status, string Subject,
        string RequesterName, bool Overdue, bool Unread, string? DuplicateOfNo, DateTimeOffset ResponseDueAt, DateTimeOffset CreatedAt);
    public sealed record File(Guid Id, string FileName, string ContentType, int SizeBytes);
    public sealed record Message(string AuthorKind, string AuthorName, string Body, bool IsInternal, List<File> Attachments);
    public sealed record Detail(Row Ticket, string? Page, string? RequesterEmail, List<Message> Messages, List<Row> Duplicates);
    public sealed record Suggestion(Guid Id, string TicketNo, string Subject, int Shops, int Requests);
    public sealed record Counts(int Open, int New, int Overdue, int Unread);

    private async Task<HttpClient> AdminAsync()
    {
        var email = $"admin-{Guid.NewGuid():N}@vendor.qa";
        var key = fixture.Factory.CreateClient();
        key.DefaultRequestHeaders.Add("X-Platform-Key", ErpFixture.PlatformKey);
        (await key.PostAsJsonAsync("/api/platform/admins", new { email, displayName = "Vendor support", password = "Admin-Password-2026" }, ErpFixture.Json))
            .EnsureSuccessStatusCode();
        var client = fixture.Factory.CreateClient();
        var login = await client.PostAsync<JsonElement>("/api/backoffice/auth/login", new { email, password = "Admin-Password-2026" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient c, string type, string subject, string body, (string Name, byte[] Data)? file = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(type), "type" }, { new StringContent(subject), "subject" }, { new StringContent(body), "body" },
            { new StringContent("en"), "language" }, { new StringContent("/sales/pos"), "page" }
        };
        if (file is { } f) form.Add(new ByteArrayContent(f.Data), "files", f.Name);
        return await c.PostAsync("/api/support/tickets", form);
    }

    private static async Task<Detail> ReplyAsync(HttpClient c, string url, string body, bool isInternal = false, string? status = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(body), "body" } };
        if (isInternal) form.Add(new StringContent("true"), "isInternal");
        if (status is not null) form.Add(new StringContent(status), "status");
        return await (await c.PostAsync(url, form)).ReadAsync<Detail>();
    }

    private async Task<HttpClient> UserAsync(TenantSession t, string email, string role)
    {
        await (await t.Client.PostJsonAsync("/api/core/users", new
        {
            email, displayNameEn = email, password = "User-Password-123", preferredLanguage = "en", roles = new[] { role }
        })).EnsureSuccessAsync();
        return await fixture.LoginAsync(t.Code, email, "User-Password-123");
    }

    [Fact]
    public async Task A_shop_opens_a_request_and_the_vendor_answers_with_internal_notes_kept_private()
    {
        var admin = await AdminAsync();
        var t = await fixture.CreateTenantAsync();
        var sender = fixture.Factory.Services.GetRequiredService<CapturingEmailSender>();

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var opened = await (await OpenAsync(t.Client, "Problem", "Receipt does not print", "The till shows an error after payment.", ("screen.png", png)))
            .ReadAsync<Detail>();
        opened.Ticket.TicketNo.ShouldStartWith("SUP-");
        opened.Ticket.Status.ShouldBe("New");
        opened.Page.ShouldBe("/sales/pos");
        opened.Messages.Single().Attachments.Single().ContentType.ShouldBe("image/png");
        // The plan's response target: test companies are on Enterprise, which promises an answer within 8 hours.
        (opened.Ticket.ResponseDueAt - opened.Ticket.CreatedAt).TotalHours.ShouldBe(8, 0.1);
        await fixture.DeliverOutboxAsync(() => sender.Sent.Any(m => m.Subject.Contains(opened.Ticket.TicketNo) && m.Subject.StartsWith("New")));

        // The vendor sees it unread, adds a note the shop never sees, then answers.
        var url = $"/api/backoffice/support/tickets/{opened.Ticket.Id}";
        (await admin.GetAsync<List<Row>>($"/api/backoffice/support/tickets?tenantId={t.TenantId}")).Single().Unread.ShouldBeTrue();
        await ReplyAsync(admin, url + "/messages", "Printer driver issue, check their Chrome flags.", isInternal: true);
        var answered = await ReplyAsync(admin, url + "/messages", "Please switch the printer off and on, then try again.");
        answered.Ticket.Status.ShouldBe("WaitingCustomer");
        answered.Messages.Count.ShouldBe(3);
        await fixture.DeliverOutboxAsync(() => sender.Sent.Any(m => m.To.Contains($"owner@{t.Code}.qa") && m.Subject.Contains(opened.Ticket.TicketNo)));
        sender.Sent.Where(m => m.To.Contains($"owner@{t.Code}.qa")).ShouldAllBe(m => !m.TextBody.Contains("driver issue"));

        // The shop sees the answer (unread), not the note, and can open the attachment.
        (await t.Client.GetAsync<JsonElement>("/api/support/unread")).GetProperty("count").GetInt32().ShouldBe(1);
        var mine = await t.Client.GetAsync<Detail>($"/api/support/tickets/{opened.Ticket.Id}");
        mine.Messages.Select(m => m.Body).ShouldBe(["The till shows an error after payment.", "Please switch the printer off and on, then try again."]);
        mine.RequesterEmail.ShouldBeNull();
        (await t.Client.GetAsync<JsonElement>("/api/support/unread")).GetProperty("count").GetInt32().ShouldBe(0);
        var image = await t.Client.GetAsync($"/api/support/attachments/{mine.Messages[0].Attachments[0].Id}");
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(png);

        // Replying brings it back to the vendor; marking it solved and replying again reopens it.
        (await ReplyAsync(t.Client, $"/api/support/tickets/{opened.Ticket.Id}/messages", "Still not printing.")).Ticket.Status.ShouldBe("InProgress");
        (await (await t.Client.PostAsync($"/api/support/tickets/{opened.Ticket.Id}/solve", null)).ReadAsync<Detail>()).Ticket.Status.ShouldBe("Solved");
        (await ReplyAsync(t.Client, $"/api/support/tickets/{opened.Ticket.Id}/messages", "It stopped again.")).Ticket.Status.ShouldBe("InProgress");

        // Closed requests take no more replies.
        await (await admin.PutJsonAsync(url, new { status = "Closed" })).EnsureSuccessAsync();
        var closed = await t.Client.PostAsync($"/api/support/tickets/{opened.Ticket.Id}/messages",
            new MultipartFormDataContent { { new StringContent("hello?"), "body" } });
        (await closed.ProblemAsync()).GetProperty("code").GetString().ShouldBe("support.closed");
    }

    [Fact]
    public async Task Shops_and_users_only_see_their_own_requests_and_employees_cannot_open_any()
    {
        var a = await fixture.CreateTenantAsync();
        var b = await fixture.CreateTenantAsync();
        var cashier = await UserAsync(a, $"cashier-{Guid.NewGuid():N}@a.qa", "salesperson");
        var owners = await (await OpenAsync(a.Client, "Question", "How do I add a branch?", "Where is it?")).ReadAsync<Detail>();
        var cashiers = await (await OpenAsync(cashier, "Problem", "Barcode scanner", "It types twice.")).ReadAsync<Detail>();

        // The owner sees the whole company's requests; the cashier only their own; another shop nothing.
        (await a.Client.GetAsync<List<Row>>("/api/support/tickets")).Select(r => r.Id).ShouldBe([cashiers.Ticket.Id, owners.Ticket.Id], ignoreOrder: true);
        (await cashier.GetAsync<List<Row>>("/api/support/tickets")).Select(r => r.Id).ShouldBe([cashiers.Ticket.Id]);
        (await cashier.GetAsync($"/api/support/tickets/{owners.Ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await b.Client.GetAsync<List<Row>>("/api/support/tickets")).ShouldBeEmpty();
        (await b.Client.GetAsync($"/api/support/tickets/{owners.Ticket.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A customer's token never opens the back-office side.
        (await a.Client.GetAsync("/api/backoffice/support/tickets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Only pictures, PDFs, sheets and text are accepted.
        var exe = await OpenAsync(a.Client, "Problem", "File", "See file", ("tool.exe", [1, 2, 3]));
        (await exe.ProblemAsync()).GetProperty("code").GetString().ShouldBe("support.file_type");

        // Employees on self-service do not contact the vendor.
        var employee = await UserAsync(a, $"emp-{Guid.NewGuid():N}@a.qa", "employee");
        (await OpenAsync(employee, "Question", "Payslip", "Where?")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Suggestions_from_several_shops_are_grouped_and_late_first_answers_are_flagged()
    {
        var admin = await AdminAsync();
        var a = await fixture.CreateTenantAsync();
        var b = await fixture.CreateTenantAsync();
        var subject = $"WhatsApp receipts {Guid.NewGuid():N}"[..40];
        var first = await (await OpenAsync(a.Client, "Suggestion", subject, "Send the receipt by WhatsApp.")).ReadAsync<Detail>();
        var second = await (await OpenAsync(b.Client, "Requirement", "Receipt on WhatsApp", "We need this.")).ReadAsync<Detail>();
        var third = await (await OpenAsync(a.Client, "Suggestion", "WhatsApp please", "Again.")).ReadAsync<Detail>();

        // Marking the others as the same request counts them on the first one.
        await (await admin.PutJsonAsync($"/api/backoffice/support/tickets/{second.Ticket.Id}", new { duplicateOfNo = first.Ticket.TicketNo })).EnsureSuccessAsync();
        await (await admin.PutJsonAsync($"/api/backoffice/support/tickets/{third.Ticket.Id}", new { duplicateOfNo = second.Ticket.TicketNo })).EnsureSuccessAsync();
        var group = (await admin.GetAsync<List<Suggestion>>("/api/backoffice/support/suggestions")).Single(s => s.Id == first.Ticket.Id);
        group.Shops.ShouldBe(2);
        group.Requests.ShouldBe(3);
        (await admin.GetAsync<Detail>($"/api/backoffice/support/tickets/{first.Ticket.Id}")).Duplicates.Count.ShouldBe(2);
        (await admin.GetAsync<Row[]>($"/api/backoffice/support/tickets?tenantId={a.TenantId}")).Single(r => r.Id == third.Ticket.Id)
            .DuplicateOfNo.ShouldBe(first.Ticket.TicketNo);
        var self = await admin.PutJsonAsync($"/api/backoffice/support/tickets/{first.Ticket.Id}", new { duplicateOfNo = third.Ticket.TicketNo });
        (await self.ProblemAsync()).GetProperty("code").GetString().ShouldBe("support.duplicate_self");

        // A request past its response time without an answer is overdue until the first answer.
        await using (var db = new Npgsql.NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"UPDATE core.support_ticket SET response_due_at = now() - interval '1 hour' WHERE id = '{first.Ticket.Id}'", db);
            await cmd.ExecuteNonQueryAsync();
        }
        (await admin.GetAsync<List<Row>>("/api/backoffice/support/tickets?status=overdue")).ShouldContain(r => r.Id == first.Ticket.Id);
        (await admin.GetAsync<Counts>("/api/backoffice/support/counts")).Overdue.ShouldBeGreaterThan(0);
        await ReplyAsync(admin, $"/api/backoffice/support/tickets/{first.Ticket.Id}/messages", "Noted, it is on our list.", status: "InProgress");
        (await admin.GetAsync<List<Row>>("/api/backoffice/support/tickets?status=overdue")).ShouldNotContain(r => r.Id == first.Ticket.Id);
    }
}
