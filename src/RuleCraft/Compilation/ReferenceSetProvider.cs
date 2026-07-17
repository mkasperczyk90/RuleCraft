using System.Reflection;
using Microsoft.CodeAnalysis;

namespace RuleCraft.Compilation;

/// <summary>
/// Builds the metadata reference set for generated-rule compilations from the host's
/// trusted platform assemblies (TPA), restricted to a small BCL whitelist plus the
/// RuleCraft, contract and context assemblies. Using host assemblies (instead of a
/// fixed reference-assembly pack) keeps versions consistent with whatever TFM the
/// host and its contract assemblies target.
/// </summary>
internal static class ReferenceSetProvider
{
    // Keep this list minimal: it is the first security layer. System.Private.CoreLib must be
    // included (facades type-forward into it), which drags some undesirable surface along —
    // the SecurityAnalyzer is the layer that actually bans System.IO/Reflection/etc.
    private static readonly HashSet<string> AllowedBclAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Runtime.Extensions",
        "netstandard",
        "mscorlib",
        "System.Collections",
        "System.Linq",
        "System.ObjectModel",
        "System.Text.RegularExpressions",
        "System.Globalization",
        "System.Memory",
        "System.Runtime.Numerics",
    };

    private static readonly Lazy<IReadOnlyList<MetadataReference>> BclReferences = new(BuildBclReferences);

    public static IReadOnlyList<MetadataReference> Build(IEnumerable<Assembly> hostAssemblies)
    {
        var references = new List<MetadataReference>(BclReferences.Value);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in hostAssemblies)
        {
            if (string.IsNullOrEmpty(assembly.Location))
                throw new NotSupportedException(
                    $"Assembly '{assembly.GetName().Name}' has no file location (single-file publish?). " +
                    "RuleCraft v1 requires assemblies loadable from disk; single-file publish and trimming are not supported.");

            if (seen.Add(assembly.Location))
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        return references;
    }

    private static IReadOnlyList<MetadataReference> BuildBclReferences()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa || tpa.Length == 0)
            throw new NotSupportedException(
                "TRUSTED_PLATFORM_ASSEMBLIES is unavailable; RuleCraft cannot build a compilation reference set in this host.");

        var references = new List<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (AllowedBclAssemblies.Contains(Path.GetFileNameWithoutExtension(path)))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references;
    }
}
