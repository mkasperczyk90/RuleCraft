using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleCraft.Json;

/// <summary>
/// The on-disk shape of a JSON rule. <c>Disallow</c> is essential, not tidiness: without it a
/// typo like <c>"priorty"</c> silently means priority 0, and <c>"test"</c> instead of
/// <c>"tests"</c> silently produces a rule with no tests that sails through the test gate.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JsonRuleDocument
{
    public string? Name { get; set; }

    public int Priority { get; set; }

    public JsonElement When { get; set; }

    public JsonElement Then { get; set; }

    public List<JsonRuleTestCase>? Tests { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class JsonRuleTestCase
{
    public string? Name { get; set; }

    public JsonElement Context { get; set; }

    public bool Applies { get; set; }
}

/// <summary>A parsed and fully type-checked rule document.</summary>
internal sealed record JsonRuleDefinition(
    string? Name,
    int Priority,
    ConditionNode When,
    JsonElement Then,
    IReadOnlyList<JsonRuleTestCase> Tests,
    bool IsAlways);
