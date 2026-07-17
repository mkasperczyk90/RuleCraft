using System.Reflection;
using System.Runtime.Loader;

namespace RuleCraft.Loading;

/// <summary>
/// Collectible load context for a single generated rule assembly.
/// <see cref="Load"/> deliberately returns null for every dependency so that the contract,
/// context and RuleCraft assemblies always resolve to the host's copies in the default
/// context — this is the sole defense against the "cannot cast X to X" type-identity trap.
/// </summary>
internal sealed class RuleAssemblyLoadContext : AssemblyLoadContext
{
    public RuleAssemblyLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
