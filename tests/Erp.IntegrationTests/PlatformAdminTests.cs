using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class PlatformAdminTests(ErpFixture fixture)
{
    public sealed record Module(string Code, bool Enabled);
    public sealed record AuditRow(string EntityType, string Action, string? UserName);
    public sealed record AuditPage(List<AuditRow> Items, int Total, List<string> EntityTypes);
    public sealed record ReviewRow(string Login, bool IsOwner, List<string> Roles, List<string> Flags);

    [Fact]
    public async Task Modules_can_be_switched_off_and_admins_can_review_the_audit_trail_and_access()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        (await c.GetAsync<List<Module>>("/api/core/modules")).ShouldAllBe(m => m.Enabled);

        // Inventory cannot go while sales still needs it; sales can, and its API then answers 403 while HR keeps working.
        var refused = await c.PutAsJsonAsync("/api/core/modules", new { disabled = new[] { "inventory" } }, ErpFixture.Json);
        (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("core.module_required");
        (await c.PutAsJsonAsync("/api/core/modules", new { disabled = new[] { "sales" } }, ErpFixture.Json)).EnsureSuccessStatusCode();
        (await c.GetAsync("/api/sales/customers")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await c.GetAsync("/api/hr/departments")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.PutAsJsonAsync("/api/core/modules", new { disabled = Array.Empty<string>() }, ErpFixture.Json)).EnsureSuccessStatusCode();
        (await c.GetAsync("/api/sales/customers")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Changes to master data show in the audit trail with who made them.
        await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "AUD", nameEn = "Audited", nameAr = "مدقق", paymentTermsDays = 30 });
        var audit = await c.GetAsync<AuditPage>("/api/core/audit-log?entityType=customer");
        audit.Total.ShouldBeGreaterThanOrEqualTo(1);
        audit.Items.ShouldAllBe(a => a.EntityType == "customer");
        audit.Items[0].UserName.ShouldBe("Owner");
        audit.EntityTypes.ShouldContain("customer");

        // The owner has every right and no two-factor sign-in: flagged in the access review.
        var review = await c.GetAsync<List<ReviewRow>>("/api/core/access-review");
        var owner = review.Single(r => r.IsOwner);
        owner.Flags.ShouldContain("no_mfa");
    }
}
