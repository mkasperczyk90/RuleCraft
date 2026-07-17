namespace RuleCraft.Tests;

/// <summary>Predicate semantics over a richer context: enums, nested paths, collections, nulls.</summary>
public class JsonSemanticsTests
{
    private sealed record RichAction(decimal Discount);

    private sealed class RichDiscount(decimal discount) : ITestDiscount
    {
        public decimal GetDiscount(TestOrder order) => discount;
    }

    private static RuleEngine<ITestDiscount, RichOrder> Engine(
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var engine = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(autoApprove: true));
        engine.EnableJsonRules<RichAction>(then => new RichDiscount(then.Discount), comparison);
        return engine;
    }

    private static string Rule(string when) =>
        $$"""
        {
          "name": "t",
          "when": {{when}},
          "then": { "discount": 0.10 },
          "tests": [
            { "context": { "Total": 100, "Customer": { "Name": "acme", "Country": "PL", "Tier": "Gold" }, "Tags": ["promo"], "PlacedAt": "2026-07-17T10:00:00+00:00" }, "applies": true },
            { "context": { "Total": 1, "Customer": null, "Tags": [], "PlacedAt": "2020-01-01T00:00:00+00:00" }, "applies": false }
          ]
        }
        """;

    private static RichOrder Order(
        decimal total = 100m, TestCustomer? customer = null, string[]? tags = null, string placedAt = "2026-07-17T10:00:00+00:00") =>
        new(total, customer, tags ?? [], DateTimeOffset.Parse(placedAt));

    private static readonly TestCustomer Acme = new("acme", "PL", CustomerTier.Gold);

    private static async Task<IReadOnlyList<string>> Errors(string json)
    {
        var ex = Assert.Throws<RuleValidationException>(() => Engine().AddJsonRuleFromSource(json));
        return ex.Report.Diagnostics;
    }

    [Fact]
    public async Task Nested_path_matches_and_null_intermediate_is_false_not_an_exception()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(Rule("""{ "field": "Customer.Country", "op": "eq", "value": "PL" }"""));

        Assert.NotNull(engine.Resolve(Order(customer: Acme)));
        Assert.NotNull(engine.Resolve(Order(customer: new TestCustomer("x", "pl", CustomerTier.Bronze))));  // case-insensitive
        Assert.Null(engine.Resolve(Order(customer: new TestCustomer("x", "DE", CustomerTier.Bronze))));

        // Customer is null: the path short-circuits to no-match. An NRE here would be swallowed by
        // the engine's predicate guard and silently skip the rule instead.
        Assert.Null(engine.Resolve(Order(customer: null)));
    }

    [Fact]
    public async Task Enum_field_compares_by_name()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(Rule("""{ "field": "Customer.Tier", "op": "eq", "value": "gold" }"""));

        Assert.NotNull(engine.Resolve(Order(customer: Acme)));
        Assert.Null(engine.Resolve(Order(customer: new TestCustomer("x", "PL", CustomerTier.Silver))));
    }

    [Fact]
    public async Task Undeclared_enum_name_lists_the_valid_ones()
    {
        var errors = await Errors(Rule("""{ "field": "Customer.Tier", "op": "eq", "value": "Platinum" }"""));
        Assert.Contains(errors, e => e.Contains("Platinum") && e.Contains("Bronze, Silver, Gold"));
    }

    [Fact]
    public async Task Enum_numeric_string_is_rejected_not_cast_blindly()
    {
        // Enum.TryParse("5") succeeds and yields (CustomerTier)5, which is not a real tier.
        var errors = await Errors(Rule("""{ "field": "Customer.Tier", "op": "eq", "value": "5" }"""));
        Assert.Contains(errors, e => e.Contains("not a valid CustomerTier"));
    }

    [Fact]
    public async Task Collection_contains_tests_membership()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(Rule("""{ "field": "Tags", "op": "contains", "value": "promo" }"""));

        Assert.NotNull(engine.Resolve(Order(tags: ["promo", "vip"])));
        Assert.Null(engine.Resolve(Order(tags: ["vip"])));
        Assert.Null(engine.Resolve(Order(tags: [])));
    }

    [Fact]
    public async Task String_contains_tests_substring()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(Rule("""{ "field": "Customer.Name", "op": "contains", "value": "cm" }"""));

        Assert.NotNull(engine.Resolve(Order(customer: Acme)));
        Assert.Null(engine.Resolve(Order(customer: new TestCustomer("other", "PL", CustomerTier.Gold))));
    }

    [Fact]
    public async Task Date_field_orders_by_iso_value()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(
            Rule("""{ "field": "PlacedAt", "op": "gte", "value": "2026-01-01T00:00:00+00:00" }"""));

        Assert.NotNull(engine.Resolve(Order(placedAt: "2026-07-17T10:00:00+00:00")));
        Assert.Null(engine.Resolve(Order(placedAt: "2025-12-31T23:59:59+00:00")));
    }

    [Fact]
    public async Task Non_iso_date_is_rejected()
    {
        var errors = await Errors(Rule("""{ "field": "PlacedAt", "op": "gte", "value": "17.07.2026" }"""));
        Assert.Contains(errors, e => e.Contains("PlacedAt"));
    }

    [Fact]
    public async Task Derived_path_segments_like_DayOfWeek_work()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(
            Rule("""{ "field": "PlacedAt.DayOfWeek", "op": "eq", "value": "Friday" }"""));

        Assert.NotNull(engine.Resolve(Order(placedAt: "2026-07-17T10:00:00+00:00")));   // a Friday
        Assert.Null(engine.Resolve(Order(placedAt: "2026-07-18T10:00:00+00:00")));      // Saturday
    }

    [Fact]
    public async Task All_any_not_compose()
    {
        var engine = Engine();
        engine.AddJsonRuleFromSource(Rule(
            """
            { "all": [
                { "field": "Total", "op": "gte", "value": 50 },
                { "any": [
                    { "field": "Customer.Tier", "op": "eq", "value": "Gold" },
                    { "field": "Tags", "op": "contains", "value": "promo" }
                ]},
                { "not": { "field": "Customer.Country", "op": "eq", "value": "DE" } }
            ]}
            """));

        Assert.NotNull(engine.Resolve(Order(100m, Acme)));
        Assert.Null(engine.Resolve(Order(10m, Acme)));                                                    // total too low
        Assert.Null(engine.Resolve(Order(100m, new TestCustomer("x", "PL", CustomerTier.Bronze))));       // neither gold nor promo
        Assert.NotNull(engine.Resolve(Order(100m, new TestCustomer("x", "PL", CustomerTier.Bronze), ["promo"])));
        Assert.Null(engine.Resolve(Order(100m, new TestCustomer("x", "DE", CustomerTier.Gold))));         // excluded country
    }

    [Fact]
    public async Task IsNull_matches_both_a_null_value_and_a_null_intermediate()
    {
        // Needs its own test cases: the shared fixture's negative case has Customer = null, which
        // makes Customer.Country null too — so it would match, not miss.
        var engine = Engine();
        engine.AddJsonRuleFromSource(
            """
            {
              "name": "unknown-country",
              "when": { "field": "Customer.Country", "op": "isNull" },
              "then": { "discount": 0.10 },
              "tests": [
                { "name": "customer without a country", "context": { "Total": 1, "Customer": { "Name": "a", "Country": null, "Tier": "Gold" }, "Tags": [], "PlacedAt": "2026-01-01T00:00:00+00:00" }, "applies": true },
                { "name": "customer with a country", "context": { "Total": 1, "Customer": { "Name": "a", "Country": "PL", "Tier": "Gold" }, "Tags": [], "PlacedAt": "2026-01-01T00:00:00+00:00" }, "applies": false }
              ]
            }
            """);

        Assert.NotNull(engine.Resolve(Order(customer: new TestCustomer("x", null, CustomerTier.Gold))));
        Assert.Null(engine.Resolve(Order(customer: Acme)));

        // A null intermediate yields a null value, so the whole path reads as "unknown".
        Assert.NotNull(engine.Resolve(Order(customer: null)));
    }

    [Fact]
    public async Task String_comparison_can_be_made_case_sensitive_by_the_host()
    {
        var engine = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(autoApprove: true));
        engine.EnableJsonRules<RichAction>(then => new RichDiscount(then.Discount), StringComparison.Ordinal);

        engine.AddJsonRuleFromSource(Rule("""{ "field": "Customer.Country", "op": "eq", "value": "PL" }"""));

        Assert.NotNull(engine.Resolve(Order(customer: Acme)));
        Assert.Null(engine.Resolve(Order(customer: new TestCustomer("x", "pl", CustomerTier.Gold))));
    }
}
