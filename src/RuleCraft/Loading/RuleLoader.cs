namespace RuleCraft.Loading;

internal static class RuleLoader
{
    /// <summary>
    /// Loads a compiled rule assembly into its own collectible context and instantiates
    /// the single <see cref="IRule{TContract,TContext}"/> implementation it contains.
    /// Always loads from a stream — never from a file path — so no file lock is held.
    /// </summary>
    public static (IRule<TContract, TContext> Rule, RuleAssemblyLoadContext Context) Load<TContract, TContext>(
        byte[] assemblyBytes, string ruleId)
        where TContract : class
    {
        var context = new RuleAssemblyLoadContext($"RuleCraft:{ruleId}");
        try
        {
            using var stream = new MemoryStream(assemblyBytes);
            var assembly = context.LoadFromStream(stream);

            var ruleTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(IRule<TContract, TContext>).IsAssignableFrom(t))
                .ToArray();

            if (ruleTypes.Length != 1)
                throw new RuleCraftException(
                    $"Rule assembly must contain exactly one IRule<{typeof(TContract).Name}, {typeof(TContext).Name}> implementation, found {ruleTypes.Length}.");

            var rule = (IRule<TContract, TContext>)Activator.CreateInstance(ruleTypes[0])!;
            return (rule, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }
}
