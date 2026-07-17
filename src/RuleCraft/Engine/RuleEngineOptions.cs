using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RuleCraft;

/// <summary>
/// How one <see cref="RuleEngine{TContract,TContext}"/> behaves. The engine copies and validates
/// these in its constructor, so the instance you pass in stays yours: reuse it for another engine,
/// or keep mutating it, without either being able to change how a live engine treats a rule.
/// </summary>
public sealed class RuleEngineOptions
{
    /// <summary>
    /// Folder holding one source + metadata pair per rule. Created on first write.
    /// Give each engine its own: the default is relative to the process, so two engines over
    /// different contracts would otherwise land in the same folder and disown each other's rules.
    /// </summary>
    public string StorePath { get; set; } = Path.Combine(Environment.CurrentDirectory, "rules");

    /// <summary>LLM used by <c>AddRuleAsync(spec)</c>. Not required for source-based or store-reload flows.</summary>
    public IChatClient? ChatClient { get; set; }

    /// <summary>Model id passed to the chat client (never hard-coded by the library).</summary>
    public string? ModelId { get; set; }

    /// <summary>Max generate → validate → feedback iterations before giving up. At least 1.</summary>
    public int MaxGenerationAttempts { get; set; } = 3;

    /// <summary>Wall-clock cap per generated/acceptance test. Must be positive.</summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public ResolutionPolicy ResolutionPolicy { get; set; } = ResolutionPolicy.HighestPriority;

    /// <summary>
    /// When true, validated rules are loaded immediately without a human approval step.
    /// Leave false unless every spec author is fully trusted — generated code runs with
    /// the process's permissions.
    /// </summary>
    public bool AutoApprove { get; set; }

    public SecurityPolicy SecurityPolicy { get; set; } = SecurityPolicy.Default;

    /// <summary>Extra assemblies the generated code may reference (contract, context and RuleCraft are automatic).</summary>
    public IList<Assembly> AdditionalReferenceAssemblies { get; } = new List<Assembly>();

    public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>
    /// Validates and deep-copies, so nothing the caller does afterwards can reach a running engine —
    /// notably <see cref="AutoApprove"/> and <see cref="SecurityPolicy"/>, where a late edit would
    /// silently move the security bar under rules already in flight.
    /// </summary>
    internal RuleEngineOptions Snapshot()
    {
        Validate();

        var copy = new RuleEngineOptions
        {
            StorePath = StorePath,
            ChatClient = ChatClient,
            ModelId = ModelId,
            MaxGenerationAttempts = MaxGenerationAttempts,
            TestTimeout = TestTimeout,
            ResolutionPolicy = ResolutionPolicy,
            AutoApprove = AutoApprove,
            SecurityPolicy = SecurityPolicy.Snapshot(),
            LoggerFactory = LoggerFactory,
        };

        foreach (var assembly in AdditionalReferenceAssemblies)
            copy.AdditionalReferenceAssemblies.Add(assembly);

        return copy;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(StorePath))
            throw new ArgumentException("StorePath must name a folder for the rule store.", nameof(StorePath));

        // The folder itself is created lazily, on first write — but a malformed path should fail
        // here, at the composition root, not deep inside the first request that adds a rule.
        try
        {
            Path.GetFullPath(StorePath);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"StorePath '{StorePath}' is not a usable path.", nameof(StorePath), ex);
        }

        if (MaxGenerationAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MaxGenerationAttempts), MaxGenerationAttempts, "At least one generation attempt is required.");

        if (TestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(TestTimeout), TestTimeout, "The test timeout must be positive.");

        if (SecurityPolicy is null)
            throw new ArgumentNullException(nameof(SecurityPolicy), "Use SecurityPolicy.Default rather than null.");

        if (LoggerFactory is null)
            throw new ArgumentNullException(nameof(LoggerFactory), "Use NullLoggerFactory.Instance rather than null.");
    }
}
