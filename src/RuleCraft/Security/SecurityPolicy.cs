namespace RuleCraft;

/// <summary>
/// Configuration for the semantic-model security gate applied to every candidate.
/// This is a guardrail against accidentally harmful generated code and a review aid —
/// it is NOT a sandbox; loaded code runs with full process permissions.
///
/// The most specific rule wins, regardless of the order you add them:
/// <list type="number">
///   <item><see cref="BannedMembers"/> — one member of a type</item>
///   <item><see cref="AllowedTypes"/>, then <see cref="BannedTypes"/> — a whole type</item>
///   <item><see cref="AllowedNamespaces"/>, then <see cref="BannedNamespaces"/> — a namespace prefix</item>
/// </list>
/// So a namespace may be allowed while a type in it is banned, and a type allowed while one of its
/// members is banned — which is exactly how the default policy admits <c>Task</c> but not
/// <c>Task.Run</c>.
/// </summary>
public sealed class SecurityPolicy
{
    /// <summary>Namespace prefixes whose symbols are rejected.</summary>
    public ISet<string> BannedNamespaces { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Fully-qualified type names whose members are rejected.</summary>
    public ISet<string> BannedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Individual members rejected on an otherwise permitted type, as <c>Namespace.Type.Member</c>
    /// (generic types use their definition's arity, e.g. <c>System.Threading.Tasks.Task&lt;TResult&gt;.Factory</c>).
    /// Checked BEFORE <see cref="AllowedTypes"/>, which is the point: a contract may legitimately
    /// return <c>Task</c> while <c>Task.Run</c> stays out of reach.
    /// </summary>
    public ISet<string> BannedMembers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Namespace prefixes exempted from <see cref="BannedNamespaces"/>. Does not exempt a type in
    /// them from <see cref="BannedTypes"/>, nor a member from <see cref="BannedMembers"/>.
    /// </summary>
    public ISet<string> AllowedNamespaces { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Fully-qualified type names exempted from <see cref="BannedTypes"/> and from the namespace
    /// lists. Does not exempt the type's members from <see cref="BannedMembers"/>.
    /// </summary>
    public ISet<string> AllowedTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Deep copy, so an engine's policy cannot be widened after it starts accepting rules.</summary>
    internal SecurityPolicy Snapshot()
    {
        var copy = new SecurityPolicy();
        copy.BannedNamespaces.UnionWith(BannedNamespaces);
        copy.BannedTypes.UnionWith(BannedTypes);
        copy.BannedMembers.UnionWith(BannedMembers);
        copy.AllowedNamespaces.UnionWith(AllowedNamespaces);
        copy.AllowedTypes.UnionWith(AllowedTypes);
        return copy;
    }

    public static SecurityPolicy Default
    {
        get
        {
            var policy = new SecurityPolicy();

            policy.BannedNamespaces.UnionWith(new[]
            {
                "System.IO",
                "System.Net",
                "System.Reflection",
                "System.Diagnostics",
                "System.Runtime.InteropServices",
                "System.Runtime.CompilerServices",
                "System.Runtime.Loader",
                "System.Linq.Expressions",
                "System.Threading",
                "System.Security",
                "Microsoft.Win32",
            });

            policy.BannedTypes.UnionWith(new[]
            {
                "System.Activator",
                "System.AppDomain",
                "System.Environment",
                "System.GC",
                "System.Console",
                "System.Type",
                "System.Delegate",
                "System.MulticastDelegate",
            });

            // Task/CancellationToken are legitimate in async contracts and tests: a rule cannot
            // implement `Task<decimal> GetDiscountAsync(...)` without naming the namespace.
            policy.AllowedNamespaces.Add("System.Threading.Tasks");
            policy.AllowedTypes.Add("System.Threading.CancellationToken");

            // …but that must not readmit the whole namespace through the back door. A rule may name
            // Task; it may not start work of its own. Work started here outlives the test harness's
            // timeout — the one thing standing between a runaway rule and the process — and makes a
            // nonsense of the ban on threading directly above. Types and members beat the namespace
            // allow, so these hold despite it.
            policy.BannedTypes.UnionWith(new[]
            {
                "System.Threading.Tasks.Parallel",
                "System.Threading.Tasks.TaskFactory",
                "System.Threading.Tasks.TaskFactory<TResult>",
                "System.Threading.Tasks.TaskScheduler",
                "System.Threading.Tasks.TaskCompletionSource",
                "System.Threading.Tasks.TaskCompletionSource<TResult>",
            });

            policy.BannedMembers.UnionWith(new[]
            {
                // Starts work on another thread.
                "System.Threading.Tasks.Task.Run",
                "System.Threading.Tasks.Task.Start",
                "System.Threading.Tasks.Task.Factory",
                "System.Threading.Tasks.Task<TResult>.Factory",
                "System.Threading.Tasks.Task.ContinueWith",
                "System.Threading.Tasks.Task<TResult>.ContinueWith",
                // Sleeps or blocks a thread.
                "System.Threading.Tasks.Task.Delay",
                "System.Threading.Tasks.Task.Wait",
                "System.Threading.Tasks.Task.WaitAll",
                "System.Threading.Tasks.Task.WaitAny",
            });

            return policy;
        }
    }
}
