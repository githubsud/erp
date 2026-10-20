using System.Net;
using Erp.Modules.Ai.Privacy;
using Erp.Modules.Ai.Services;
using Erp.SharedKernel.Domain;

namespace Erp.UnitTests;

public sealed class AiUnitTests
{
    [Fact]
    public void Names_and_identifiers_become_placeholders_and_come_back_in_the_answer()
    {
        var filter = new PrivacyFilter(true);
        var json = filter.Json(new
        {
            Customers = new[] { new { Customer = "Al Noor", CustomerAr = "النور", Owed = 1200m }, new { Customer = "Al Noor", CustomerAr = "النور", Owed = 5m } },
            Note = "QID 28412345678, IBAN QA58DOHB00001234567890ABCDEFG, call +974 5512 3456 or mail a@b.qa"
        });

        json.ShouldNotContain("Al Noor");
        json.ShouldNotContain("النور");
        json.ShouldNotContain("28412345678");
        json.ShouldNotContain("QA58DOHB");
        json.ShouldNotContain("5512");
        json.ShouldNotContain("a@b.qa");
        json.ShouldContain("\"owed\":1200");
        json.ShouldContain("\"customer\":\"[P1]\"");       // the same name always gets the same placeholder
        json.Split("[P1]").Length.ShouldBe(3);
        filter.Restore("[P1] owes 1,200 QAR ([P2]).").ShouldBe("Al Noor owes 1,200 QAR (النور).");
        filter.Text("ref INV-2026-0001, amount 4350.00").ShouldBe("ref INV-2026-0001, amount 4350.00");
        filter.Restore("[P7] Dear [P1], please pay.").ShouldBe("Dear Al Noor, please pay.");   // made-up placeholders are dropped
        filter.PlaceholderRule.ShouldContain("[P1]");
        new PrivacyFilter(false).PlaceholderRule.ShouldBeEmpty();
    }

    [Fact]
    public void A_private_provider_sees_everything_as_it_is()
    {
        var filter = new PrivacyFilter(false);
        filter.Json(new { Customer = "Al Noor" }).ShouldBe("""{"customer":"Al Noor"}""");
        filter.Text("QID 28412345678").ShouldBe("QID 28412345678");
    }

    [Fact]
    public void Long_tool_results_are_cut()
    {
        var json = new PrivacyFilter(false).Json(new { rows = Enumerable.Range(0, 5000).Select(i => new { item = $"Item {i}", qty = i }) });
        json.Length.ShouldBeLessThan(PrivacyFilter.MaxJsonLength + 20);
        json.ShouldEndWith("…(cut)");
    }

    [Theory]
    [InlineData("5512 3456", "https://wa.me/97455123456?text=Hi%20there")]
    [InlineData("+974 5512-3456", "https://wa.me/97455123456?text=Hi%20there")]
    [InlineData("0097455123456", "https://wa.me/97455123456?text=Hi%20there")]
    [InlineData(null, "https://wa.me/?text=Hi%20there")]
    public void Whatsapp_links_take_qatar_numbers(string? phone, string expected) => DraftService.WhatsAppUrl(phone, "Hi there").ShouldBe(expected);

    [Fact]
    public void Document_fields_are_read_from_the_models_json()
    {
        var f = DocumentReader.Parse("""```json {"name_en":"ANA REYES","id_number":"29160812345","date_of_birth":"12/03/1991","expiry_date":"2027-01-31","gender":"female","occupation":"null"} ```""");
        f.NameEn.ShouldBe("ANA REYES");
        f.IdNumber.ShouldBe("29160812345");
        f.ExpiryDate.ShouldBe("2027-01-31");
        f.Gender.ShouldBe("F");
        f.Occupation.ShouldBeNull();
        Should.Throw<BusinessRuleException>(() => DocumentReader.Parse("I cannot read this image."));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("1.1.1.1", false)]
    [InlineData("142.250.74.110", false)]
    public void Internal_addresses_are_recognised(string ip, bool expected) => AiSettingsService.IsInternal(IPAddress.Parse(ip)).ShouldBe(expected);
}
