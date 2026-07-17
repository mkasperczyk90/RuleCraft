using System.Text.RegularExpressions;

namespace RuleCraft.Generation;

internal static partial class CodeExtractor
{
    [GeneratedRegex(@"```[a-zA-Z0-9#+._-]*\s*\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    /// <summary>Extracts the largest fenced block (csharp, json, …), or the whole text when no fence is present.</summary>
    public static string Extract(string llmResponse)
    {
        var matches = FenceRegex().Matches(llmResponse);
        if (matches.Count == 0)
            return llmResponse.Trim();

        return matches
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(s => s.Length)
            .First()
            .Trim();
    }
}
