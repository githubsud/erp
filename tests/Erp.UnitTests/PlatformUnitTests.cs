using System.Reflection;
using Erp.Api;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Finance.Domain;
using Erp.Modules.Finance.Posting;
using Erp.SharedKernel.Domain;

namespace Erp.UnitTests;

public sealed class ArchitectureTests
{
    /// <summary>A module may use another module only through its public Contracts assembly.</summary>
    [Fact]
    public void Modules_reference_other_modules_only_through_contracts()
    {
        var modules = ModuleCatalog.All.Select(m => m.Assembly).ToList();
        var violations = new List<string>();
        foreach (var assembly in modules)
        {
            var self = assembly.GetName().Name!;
            foreach (var reference in assembly.GetReferencedAssemblies().Select(r => r.Name!))
                if (reference.StartsWith("Erp.Modules.", StringComparison.Ordinal) && reference != self && !reference.EndsWith(".Contracts", StringComparison.Ordinal))
                    violations.Add($"{self} → {reference}");
        }
        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Contracts_depend_only_on_the_shared_kernel()
    {
        var contracts = typeof(Erp.Modules.Finance.Contracts.IPostingService).Assembly;
        contracts.GetReferencedAssemblies().Select(r => r.Name!)
            .Where(n => n.StartsWith("Erp.", StringComparison.Ordinal))
            .ShouldAllBe(n => n == "Erp.SharedKernel");
    }

    [Fact]
    public void Ledger_entities_are_immutable()
    {
        typeof(IImmutable).IsAssignableFrom(typeof(JournalEntry)).ShouldBeTrue();
        typeof(IImmutable).IsAssignableFrom(typeof(JournalLine)).ShouldBeTrue();
    }

    [Fact]
    public void Money_is_never_floating_point()
    {
        var offenders = ModuleCatalog.All.Select(m => m.Assembly)
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(Entity).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float) ||
                            p.PropertyType == typeof(double?) || p.PropertyType == typeof(float?))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();
        offenders.ShouldBeEmpty();
    }
}

public sealed class SharedKernelTests
{
    [Theory]
    [InlineData("SoftClosed", "soft_closed")]
    [InlineData("Open", "open")]
    [InlineData("BasicPlusFixedAllowances", "basic_plus_fixed_allowances")]
    public void Enums_are_stored_as_snake_case(string name, string expected) => SnakeCaseEnumConverter.ToSnakeCase(name).ShouldBe(expected);

    [Fact]
    public void Bilingual_copies_the_missing_language()
    {
        Bilingual.Require("Basic", null).ShouldBe(("Basic", "Basic"));
        Bilingual.Require(null, "أساسي").ShouldBe(("أساسي", "أساسي"));
        Should.Throw<BusinessRuleException>(() => Bilingual.Require(" ", null));
    }

    [Fact]
    public void Money_rounds_half_away_from_zero()
    {
        Money.Round(2.345m).ShouldBe(2.35m);
        Money.Round(-2.345m).ShouldBe(-2.35m);
    }
}

public sealed class PostingRuleResolutionTests
{
    private static PostingRule Rule(string role, Guid? key, Guid account) => new() { AccountRole = role, RoleKey = key, AccountId = account };

    [Fact]
    public void Keyed_rule_wins_over_role_default()
    {
        var key = Guid.NewGuid();
        var specific = Guid.NewGuid();
        var fallback = Guid.NewGuid();
        var rules = new[] { Rule("hr.payroll_expense", null, fallback), Rule("hr.payroll_expense", key, specific) };

        PostingService.Resolve(rules, "hr.payroll_expense", key).ShouldBe(specific);
        PostingService.Resolve(rules, "hr.payroll_expense", Guid.NewGuid()).ShouldBe(fallback);
        PostingService.Resolve(rules, "hr.payroll_expense", null).ShouldBe(fallback);
        Should.Throw<BusinessRuleException>(() => PostingService.Resolve(rules, "fin.unknown", null)).Code.ShouldBe("fin.posting_rule_missing");
    }
}
