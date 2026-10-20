using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Erp.IntegrationTests.Infrastructure;
using Erp.Modules.Ai.Providers;
using Npgsql;

namespace Erp.IntegrationTests;

[Collection(ErpCollection.Name)]
public sealed class AiTests(ErpFixture fixture)
{
    private static readonly DateOnly Today = FinanceTests.Today;

    public sealed record Ask(string Answer, List<string> ToolsUsed, string Provider, bool Private);
    public sealed record Draft(string Text, string? WhatsAppUrl, string Provider);
    public sealed record Summary(string Text, string Facts, string Provider, string Language);
    public sealed record Settings(string Mode, string? ProviderCode, bool HasApiKey, Effective? Effective);
    public sealed record Effective(string Provider, bool IsPrivate, int UsedToday, int? DailyLimit);
    public sealed record Fields(string? NameEn, string? IdNumber, string? Nationality, string? DateOfBirth, string? ExpiryDate, string? Gender);

    private async Task SqlAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.OwnerConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A shared (not private) platform provider, as Google's free tier is.</summary>
    private async Task UseSharedProviderAsync(HttpClient c, string code = "shared-test", int limit = 100)
    {
        await SqlAsync($"""
            INSERT INTO ai.provider (id, code, name, kind, base_url, model, is_private, is_default, is_enabled, daily_limit)
            VALUES (gen_random_uuid(), '{code}', 'Shared test', 'open_ai_compatible', 'https://example.invalid/v1/', 'm', false, false, true, {limit})
            ON CONFLICT (code) DO NOTHING
            """);
        await (await c.PutJsonAsync("/api/ai/settings", new { mode = "Platform", providerCode = code })).EnsureSuccessAsync();
    }

    private static async Task<Guid> OwingCustomerAsync(HttpClient c, string name, decimal amount)
    {
        var customer = (await c.PostAsync<JsonElement>("/api/sales/customers", new { code = "C-" + name[..3].ToUpperInvariant(), nameEn = name, nameAr = "نور" }))
            .GetProperty("id").GetGuid();
        var invoice = await c.PostAsync<JsonElement>("/api/sales/invoices", new
        {
            customerId = customer, invoiceDate = Today, lines = new[] { new { description = "Abaya stitching", quantity = 1m, unitPrice = amount } }
        });
        await c.PostAsync<JsonElement>($"/api/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { });
        return customer;
    }

    /// <summary>First round asks for customer balances; the second answers with whatever the tool result says the top customer is.</summary>
    private static AiResponse BalancesScript(AiEndpoint endpoint, AiRequest request)
    {
        var toolResult = request.Messages.LastOrDefault(m => m.Role == "tool");
        if (toolResult is null) return new AiResponse(null, [new AiToolCall("c1", "customer_balances", """{"top":3}""")], 100, 10);
        var top = JsonDocument.Parse(toolResult.Text!).RootElement.GetProperty("customers")[0];
        return new AiResponse($"{top.GetProperty("customer").GetString()} owes the most: {top.GetProperty("owed").GetDecimal():0.00} QAR.", [], 200, 20);
    }

    [Fact]
    public async Task Ask_answers_from_tools_and_a_shared_provider_never_sees_names()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await OwingCustomerAsync(c, "Al Noor Abayas", 1_150m);
        await UseSharedProviderAsync(c);
        fixture.Ai.Reset(BalancesScript);

        var answer = await c.PostAsync<Ask>("/api/ai/ask", new { question = "Who owes me the most? My QID is 28412345678." });

        answer.Answer.ShouldBe("Al Noor Abayas owes the most: 1150.00 QAR.");
        answer.ToolsUsed.ShouldBe(["customer_balances"]);
        answer.Private.ShouldBeFalse();
        var sent = string.Join("\n", fixture.Ai.Calls.SelectMany(x => x.Request.Messages).Select(m => m.Text));
        sent.ShouldNotContain("Al Noor");
        sent.ShouldNotContain("28412345678");
        sent.ShouldContain("[P");
        fixture.Ai.Calls.First().Request.Tools!.Select(x => x.Name).ShouldContain("customer_balances");

        // Usage is counted against the platform provider's daily allowance.
        var settings = await c.GetAsync<Settings>("/api/ai/settings");
        settings.Effective!.UsedToday.ShouldBe(2);
        settings.Effective.DailyLimit.ShouldBe(100);
    }

    [Fact]
    public async Task A_private_provider_gets_the_real_names_and_tools_follow_permissions_and_modules()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await OwingCustomerAsync(c, "Salma Tailoring", 300m);
        fixture.Ai.Reset(BalancesScript);

        var answer = await c.PostAsync<Ask>("/api/ai/ask", new { question = "من أكثر عميل مديون؟" });

        answer.Private.ShouldBeTrue();
        answer.Answer.ShouldStartWith("Salma Tailoring");
        fixture.Ai.Calls.Last().Request.Messages.Single(m => m.Role == "tool").Text!.ShouldContain("Salma Tailoring");

        // Switching Sales off removes its tools.
        var tools = await c.GetAsync<List<JsonElement>>("/api/ai/tools");
        tools.Select(x => x.GetProperty("name").GetString()).ShouldContain("customer_balances");
        await (await c.PutJsonAsync("/api/core/modules", new { disabled = new[] { "sales" } })).EnsureSuccessAsync();
        (await c.GetAsync<List<JsonElement>>("/api/ai/tools")).Select(x => x.GetProperty("name").GetString()).ShouldNotContain("customer_balances");

        // And switching the assistant off closes the whole API.
        await (await c.PutJsonAsync("/api/core/modules", new { disabled = new[] { "ai" } })).EnsureSuccessAsync();
        (await c.PostJsonAsync("/api/ai/ask", new { question = "hello" })).IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task The_daily_allowance_of_a_platform_provider_is_enforced()
    {
        var t = await fixture.CreateTenantAsync();
        await UseSharedProviderAsync(t.Client, "limited-test", limit: 1);
        fixture.Ai.Reset((_, _) => new AiResponse("Fine.", [], 5, 1));

        await t.Client.PostAsync<Ask>("/api/ai/ask", new { question = "How is business?" });
        var second = await t.Client.PostJsonAsync("/api/ai/ask", new { question = "And now?" });

        second.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await second.ProblemAsync()).GetProperty("code").GetString().ShouldBe("ai.daily_limit");
    }

    [Fact]
    public async Task A_company_can_use_its_own_subscription_with_the_key_encrypted_and_never_returned()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;

        // Internal addresses are refused, so the server cannot be pointed at its own network.
        foreach (var url in new[] { "http://api.openai.com/v1/", "https://127.0.0.1/v1/", "https://10.0.0.5/v1/", "https://169.254.169.254/" })
        {
            var refused = await c.PutJsonAsync("/api/ai/settings", new { mode = "Own", kind = "OpenAiCompatible", baseUrl = url, model = "gpt", apiKey = "sk-x" });
            (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("ai.url_invalid");
        }

        await (await c.PutJsonAsync("/api/ai/settings", new
        {
            mode = "Own", kind = "OpenAiCompatible", baseUrl = "https://1.1.1.1/v1/", model = "gpt-test", visionModel = "gpt-vision", apiKey = "sk-secret-123"
        })).EnsureSuccessAsync();
        var settings = await c.GetAsync<Settings>("/api/ai/settings");
        settings.Mode.ShouldBe("Own");
        settings.HasApiKey.ShouldBeTrue();
        (await c.GetStringAsync("/api/ai/settings")).ShouldNotContain("sk-secret");

        await using (var connection = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT api_key FROM ai.setting WHERE tenant_id = '{t.TenantId}'", connection);
            ((string)(await cmd.ExecuteScalarAsync())!).ShouldStartWith("v1:");
        }

        fixture.Ai.Reset();
        var test = await (await c.PostAsync("/api/ai/settings/test", null)).ReadAsync<JsonElement>();
        test.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var (endpoint, _) = fixture.Ai.Calls.Single();
        endpoint.ProviderCode.ShouldBe("own");
        endpoint.ApiKey.ShouldBe("sk-secret-123");
        endpoint.IsPrivate.ShouldBeTrue();
        endpoint.DailyLimit.ShouldBeNull();

        // Saving again without a key keeps the stored one.
        await (await c.PutJsonAsync("/api/ai/settings", new { mode = "Own", kind = "OpenAiCompatible", baseUrl = "https://1.1.1.1/v1/", model = "gpt-2" }))
            .EnsureSuccessAsync();
        (await c.GetAsync<Settings>("/api/ai/settings")).HasApiKey.ShouldBeTrue();
    }

    [Fact]
    public async Task Whatsapp_drafts_keep_names_away_from_a_shared_provider()
    {
        var t = await fixture.CreateTenantAsync();
        await UseSharedProviderAsync(t.Client);
        fixture.Ai.Reset((_, r) =>
        {
            var facts = r.Messages[0].Text!;
            var token = facts.Split('\n').Single(l => l.StartsWith("- customer:")).Split(": ")[1];
            return new AiResponse($"Dear {token}, a friendly reminder: 450 QAR is due.", [], 50, 20);
        });

        var draft = await t.Client.PostAsync<Draft>("/api/ai/draft", new
        {
            kind = "payment_reminder", language = "en", phone = "5512 3456",
            facts = new Dictionary<string, string> { ["customer"] = "Mariam Al-Kuwari", ["amount"] = "450.00", ["due_date"] = "2026-10-01" }
        });

        draft.Text.ShouldBe("Dear Mariam Al-Kuwari, a friendly reminder: 450 QAR is due.");
        draft.WhatsAppUrl.ShouldStartWith("https://wa.me/97455123456?text=Dear%20Mariam");
        fixture.Ai.Calls.Single().Request.Messages[0].Text!.ShouldNotContain("Mariam");
        (await (await t.Client.PostJsonAsync("/api/ai/draft", new { kind = "spam", language = "en" })).ProblemAsync())
            .GetProperty("code").GetString().ShouldBe("ai.draft_kind");
    }

    [Fact]
    public async Task The_daily_summary_is_written_from_dashboard_figures_and_falls_back_to_the_figures()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await OwingCustomerAsync(c, "Fatima Salon", 700m);
        fixture.Ai.Reset((_, _) => new AiResponse("- Receivables are 700.00 QAR.", [], 300, 30));

        var summary = await c.PostAsync<Summary>("/api/ai/summary", new { language = "en" });
        summary.Text.ShouldBe("- Receivables are 700.00 QAR.");
        summary.Facts.ShouldContain("Receivables: 700.00 QAR");
        fixture.Ai.Calls.Single().Request.Messages[0].Text!.ShouldContain("Receivables: 700.00 QAR");
        (await c.GetAsync<Summary>("/api/ai/summary/latest?language=en")).Text.ShouldBe(summary.Text);

        // When the AI fails, the owner still gets the figures.
        fixture.Ai.Reset((_, _) => throw new AiProviderException("down"));
        var fallback = await c.PostAsync<Summary>("/api/ai/summary", new { language = "en" });
        fallback.Provider.ShouldBe("none");
        fallback.Text.ShouldContain("Receivables: 700.00 QAR");
    }

    [Fact]
    public async Task Id_photos_are_read_only_by_a_private_model_that_can_see_images()
    {
        var t = await fixture.CreateTenantAsync();
        var c = t.Client;
        await UseSharedProviderAsync(c);
        static MultipartFormDataContent Photo()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "qid.jpg");
            return form;
        }

        var refused = await c.PostAsync("/api/ai/read-document?kind=qid", Photo());
        (await refused.ProblemAsync()).GetProperty("code").GetString().ShouldBe("ai.document_private");

        await (await c.PutJsonAsync("/api/ai/settings", new
        {
            mode = "Own", kind = "Anthropic", baseUrl = "https://1.1.1.1", model = "claude-test", apiKey = "sk-ant"
        })).EnsureSuccessAsync();
        fixture.Ai.Reset((_, r) => new AiResponse(
            """Here: {"name_en":"RAJESH KUMAR","id_number":"28435612345","nationality":"India","date_of_birth":"1984-03-12","expiry_date":"2027-05-01","gender":"Male","name_ar":null}""",
            [], 900, 60));
        var fields = await (await c.PostAsync("/api/ai/read-document?kind=qid", Photo())).ReadAsync<Fields>();

        fields.ShouldBe(new Fields("RAJESH KUMAR", "28435612345", "India", "1984-03-12", "2027-05-01", "M"));
        var (endpoint, request) = fixture.Ai.Calls.Single();
        request.Vision.ShouldBeTrue();
        request.Messages[0].Image!.MediaType.ShouldBe("image/jpeg");
        endpoint.Kind.ShouldBe(Erp.Modules.Ai.Domain.AiProviderKind.Anthropic);
    }

    [Fact]
    public async Task Only_platform_admins_manage_the_platform_providers()
    {
        var t = await fixture.CreateTenantAsync();
        (await t.Client.GetAsync("/api/backoffice/ai/providers")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
