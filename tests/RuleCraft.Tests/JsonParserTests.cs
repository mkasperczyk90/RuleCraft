using System.Text;

namespace RuleCraft.Tests;

/// <summary>
/// The DSL's job is to make malformed rules loud. Every test here is a mistake that would
/// otherwise be silent — a rule that quietly never matches, or matches everything.
/// </summary>
public class JsonParserTests
{
    private static string Rule(string when, string tests = """
        [ { "context": { "Total": 150, "Customer": "alice", "ItemCount": 1 }, "applies": true },
          { "context": { "Total": 1, "Customer": "bob", "ItemCount": 0 }, "applies": false } ]
        """) =>
        $$"""
        { "name": "t", "when": {{when}}, "then": { "discount": 0.10 }, "tests": {{tests}} }
        """;

    private static async Task<IReadOnlyList<string>> Errors(string json, RuleEngine<ITestDiscount, TestOrder>? engine = null)
    {
        engine ??= Fixtures.JsonEngine();
        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(json));
        Assert.Empty(ex.Report.SecurityFindings);
        return ex.Report.Diagnostics;
    }

    // ---------------------------------------------------------------- the silent-mismatch bug

    [Fact]
    public async Task Integer_literal_matches_a_decimal_field()
    {
        // The trap: JSON 500 read as long, compared to a boxed decimal 500m, is NOT equal.
        // Coercing to the field's CLR type at parse time is what makes this work.
        var engine = Fixtures.JsonEngine(autoApprove: true);
        engine.AddJsonRuleFromSource(Rule("""{ "field": "Total", "op": "eq", "value": 500 }""",
            """
            [ { "context": { "Total": 500, "Customer": "a", "ItemCount": 1 }, "applies": true },
              { "context": { "Total": 499, "Customer": "a", "ItemCount": 1 }, "applies": false } ]
            """));

        Assert.NotNull(engine.Resolve(new TestOrder(500m, "a", 1)));
        Assert.Null(engine.Resolve(new TestOrder(499m, "a", 1)));
    }

    [Fact]
    public async Task Decimal_literal_matches_an_int_field()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);
        engine.AddJsonRuleFromSource(Rule("""{ "field": "ItemCount", "op": "gte", "value": 3 }""",
            """
            [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 5 }, "applies": true },
              { "context": { "Total": 1, "Customer": "a", "ItemCount": 2 }, "applies": false } ]
            """));

        Assert.NotNull(engine.Resolve(new TestOrder(1m, "a", 5)));
        Assert.Null(engine.Resolve(new TestOrder(1m, "a", 2)));
    }

    [Fact]
    public async Task Fractional_value_on_an_int_field_is_rejected()
    {
        var errors = await Errors(Rule("""{ "field": "ItemCount", "op": "eq", "value": 1.5 }"""));
        Assert.Contains(errors, e => e.Contains("ItemCount") && e.Contains("Int32"));
    }

    // ---------------------------------------------------------------- fields

    [Fact]
    public async Task Unknown_field_error_lists_the_available_fields()
    {
        var errors = await Errors(Rule("""{ "field": "Totl", "op": "gte", "value": 100 }"""));

        var error = Assert.Single(errors);
        Assert.Contains("Totl", error);
        Assert.Contains("Total", error);
        Assert.Contains("Customer", error);
        Assert.Contains("ItemCount", error);
    }

    [Fact]
    public async Task Field_names_are_case_insensitive()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);
        engine.AddJsonRuleFromSource(Rule("""{ "field": "total", "op": "gte", "value": 100 }"""));

        Assert.NotNull(engine.Resolve(new TestOrder(150m, "a", 1)));
    }

    [Fact]
    public async Task Non_numeric_value_on_a_decimal_field_names_field_type_and_value()
    {
        var errors = await Errors(Rule("""{ "field": "Total", "op": "gte", "value": "abc" }"""));

        var error = Assert.Single(errors);
        Assert.Contains("\"abc\"", error);
        Assert.Contains("Decimal", error);
        Assert.Contains("Total", error);
    }

    // ---------------------------------------------------------------- operators

    [Fact]
    public async Task Ordering_operator_on_a_string_field_is_rejected()
    {
        var errors = await Errors(Rule("""{ "field": "Customer", "op": "gt", "value": "m" }"""));
        Assert.Contains(errors, e => e.Contains("gt") && e.Contains("String"));
    }

    [Fact]
    public async Task Unknown_operator_lists_the_valid_ones()
    {
        var errors = await Errors(Rule("""{ "field": "Total", "op": "greaterThan", "value": 100 }"""));
        Assert.Contains(errors, e => e.Contains("greaterThan") && e.Contains("gte"));
    }

    [Fact]
    public async Task IsNull_on_a_non_nullable_field_is_rejected()
    {
        // Always false — a constant dressed up as a condition.
        var errors = await Errors(Rule("""{ "field": "Total", "op": "isNull" }"""));
        Assert.Contains(errors, e => e.Contains("Total") && e.Contains("never be null"));
    }

    [Fact]
    public async Task IsNull_on_a_non_nullable_reference_field_is_rejected_and_names_the_fix()
    {
        // `Customer` is declared `string`, not `string?`. Without reading the annotation this looks
        // nullable and the rule would silently never match — the exact failure the value-type check
        // above already prevents.
        var errors = await Errors(Rule("""{ "field": "Customer", "op": "isNull" }"""));

        Assert.Contains(errors, e => e.Contains("Customer") && e.Contains("never be null"));
        Assert.Contains(errors, e => e.Contains("String?"));
    }

    [Fact]
    public async Task In_requires_a_non_empty_array()
    {
        Assert.Contains(await Errors(Rule("""{ "field": "Customer", "op": "in", "value": "alice" }""")),
            e => e.Contains("must be an array"));

        Assert.Contains(await Errors(Rule("""{ "field": "Customer", "op": "in", "value": [] }""")),
            e => e.Contains("empty array"));
    }

    [Fact]
    public async Task In_with_a_wrong_typed_element_names_the_index()
    {
        var errors = await Errors(Rule("""{ "field": "Total", "op": "in", "value": [100, "abc"] }"""));
        Assert.Contains(errors, e => e.Contains("[1]"));
    }

    [Fact]
    public async Task Missing_value_is_rejected()
    {
        Assert.Contains(await Errors(Rule("""{ "field": "Total", "op": "gte" }""")),
            e => e.Contains("missing 'value'"));
    }

    // ---------------------------------------------------------------- structure

    [Fact]
    public async Task Empty_all_and_empty_any_are_rejected()
    {
        Assert.Contains(await Errors(Rule("""{ "all": [] }""")), e => e.Contains("every context"));
        Assert.Contains(await Errors(Rule("""{ "any": [] }""")), e => e.Contains("no context"));
    }

    [Fact]
    public async Task Always_false_is_rejected()
    {
        Assert.Contains(await Errors(Rule("""{ "always": false }""")), e => e.Contains("never apply"));
    }

    [Fact]
    public async Task Mixed_condition_forms_are_rejected()
    {
        Assert.Contains(await Errors(Rule("""{ "all": [ { "always": true } ], "field": "Total" }""")),
            e => e.Contains("mixes condition forms"));
    }

    [Fact]
    public async Task Unrecognised_condition_shape_explains_the_grammar()
    {
        Assert.Contains(await Errors(Rule("""{ "totalOver": 100 }""")), e => e.Contains("is not a condition"));
    }

    [Fact]
    public async Task Misspelled_top_level_property_is_rejected_not_silently_defaulted()
    {
        // "priorty" would silently mean priority 0; "test" would silently mean "no tests".
        var errors = await Errors(
            """
            { "name": "t", "priorty": 5, "when": { "always": true }, "then": { "discount": 0.1 },
              "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ] }
            """);
        Assert.Contains(errors, e => e.Contains("priorty"));
    }

    [Fact]
    public async Task Missing_when_or_then_is_rejected()
    {
        Assert.Contains(await Errors("""{ "name": "t", "then": { "discount": 0.1 } }"""),
            e => e.Contains("'when'"));

        Assert.Contains(await Errors(
                """
                { "name": "t", "when": { "always": true },
                  "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ] }
                """),
            e => e.Contains("'then'"));
    }

    [Fact]
    public async Task Malformed_json_is_a_diagnostic_not_a_crash()
    {
        Assert.NotEmpty(await Errors("{ this is not json"));
    }

    [Fact]
    public async Task Document_deeper_than_the_limit_is_rejected_without_stack_overflow()
    {
        var when = new StringBuilder();
        for (var i = 0; i < 40; i++)
            when.Append("""{ "not": """);
        when.Append("""{ "always": true }""");
        for (var i = 0; i < 40; i++)
            when.Append('}');

        Assert.NotEmpty(await Errors(Rule(when.ToString())));
    }

    [Fact]
    public async Task Oversized_document_is_rejected()
    {
        var padding = new string('x', 70 * 1024);
        var errors = await Errors($$"""
            { "name": "{{padding}}", "when": { "always": true }, "then": { "discount": 0.1 },
              "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ] }
            """);
        Assert.Contains(errors, e => e.Contains("larger than"));
    }

    // ---------------------------------------------------------------- test coverage gate

    [Fact]
    public async Task A_rule_with_no_negative_test_case_is_rejected()
    {
        var errors = await Errors(Rule("""{ "field": "Total", "op": "gte", "value": 100 }""",
            """[ { "context": { "Total": 150, "Customer": "a", "ItemCount": 1 }, "applies": true } ]"""));
        Assert.Contains(errors, e => e.Contains("\"applies\": false"));
    }

    [Fact]
    public async Task A_rule_with_no_positive_test_case_is_rejected()
    {
        var errors = await Errors(Rule("""{ "field": "Total", "op": "gte", "value": 100 }""",
            """[ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": false } ]"""));
        Assert.Contains(errors, e => e.Contains("\"applies\": true"));
    }

    [Fact]
    public async Task An_always_rule_needs_only_a_positive_case()
    {
        // A blanket rule cannot demonstrate a non-matching context, so the negative case is waived.
        var engine = Fixtures.JsonEngine(autoApprove: true);
        engine.AddJsonRuleFromSource(Rule("""{ "always": true }""",
            """[ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]"""));

        Assert.NotNull(engine.Resolve(new TestOrder(1m, "a", 1)));
    }
}
