using Microsoft.Extensions.Logging;
using RuleCraft.Engine;
using RuleCraft.Generation;
using RuleCraft.Json;
using RuleCraft.Store;

namespace RuleCraft;

/// <summary>
/// The RuleCraft engine for one contract/context pair.
///
/// Lifecycle of a rule:
/// spec → LLM generation (or hand-written source) → compile/parse → security analysis →
/// generated + acceptance tests → persisted as <see cref="RuleStatus.PendingApproval"/> →
/// human approval (<see cref="Approve"/>, typically exposed as an HTTP endpoint) →
/// loaded into the running application and dispatchable via <see cref="Resolve"/>.
///
/// Three kinds of rule share all of that and compete purely by priority:
/// compiled C# (<see cref="AddRuleAsync"/>), JSON-DSL (<see cref="AddJsonRuleAsync"/>, requires
/// <see cref="EnableJsonRules{TThen}"/>) and static host code (<see cref="AddStaticRule"/>).
///
/// Only the two generation methods are asynchronous, because only they do I/O — the call to the
/// LLM. Everything else (compiling, parsing, running tests, approving) is CPU-bound work that
/// runs on the calling thread; offload it with <c>Task.Run</c> if a request thread must not block.
///
/// The engine is a thread-safe singleton: <see cref="Resolve"/> is lock-free, and mutations
/// are serialized per rule id. <see cref="Dispose"/> unloads every rule assembly it owns.
/// </summary>
public sealed class RuleEngine<TContract, TContext> : IDisposable where TContract : class
{
    private readonly RuleEngineOptions _options;
    private readonly IRuleStore _store;
    private readonly RuleRegistry<TContract, TContext> _registry = new();
    private readonly RuleLocks _locks = new();
    private readonly CSharpRuleKind<TContract, TContext> _csharp;
    private readonly List<IRuleAcceptanceTest<TContract>> _acceptanceTests = [];
    private readonly ILogger _logger;
    private IRuleKind<TContract, TContext>? _json;
    private RulePrompts? _jsonPrompts;
    private TContract? _fallback;
    // Volatile: Dispose runs on one thread while Resolve reads this on others, so the write must be
    // visible without a lock (Resolve is deliberately lock-free).
    private volatile bool _disposed;

    // Reflected once per closed generic type, not per rule: the fingerprint walks every public
    // member of both types.
    private static readonly string ContractTypeName = typeof(TContract).FullName ?? typeof(TContract).Name;
    private static readonly string ContextTypeName = typeof(TContext).FullName ?? typeof(TContext).Name;
    private static readonly string CurrentFingerprint = ContractFingerprint.Compute(typeof(TContract), typeof(TContext));

    public RuleEngine(RuleEngineOptions? options = null)
    {
        _options = (options ?? new RuleEngineOptions()).Snapshot();
        _logger = _options.LoggerFactory.CreateLogger($"RuleCraft.RuleEngine<{typeof(TContract).Name}>");
        _store = _options.Store ?? new FileRuleStore(_options.StorePath, _logger);
        _csharp = new CSharpRuleKind<TContract, TContext>(_options, AcceptanceTests);
    }

    /// <summary>
    /// True for a rule some other engine wrote into this folder. Not the same thing as a rule whose
    /// contract has since changed shape — that one is ours and stale, and quarantining it is right.
    /// This one was never ours, and quarantining it would be a permanent disk write punishing a rule
    /// for a mistake in the host's configuration.
    ///
    /// Null names mean metadata written before the fields existed: assume it is ours, as the store
    /// always did.
    /// </summary>
    private static bool IsForAnotherContract(RuleRecord metadata) =>
        (metadata.ContractType is not null && metadata.ContractType != ContractTypeName)
        || (metadata.ContextType is not null && metadata.ContextType != ContextTypeName);

    /// <summary>Says which of the two happened, instead of guessing in the status reason.</summary>
    private static string RevalidationFailure(RuleRecord metadata) =>
        metadata.ContractFingerprint == CurrentFingerprint
            ? "Failed revalidation: the rule no longer passes its own gates."
            : $"Failed revalidation: {ContractTypeName} or {ContextTypeName} changed shape since this rule was approved.";

    private IReadOnlyList<IRuleAcceptanceTest<TContract>> AcceptanceTests()
    {
        lock (_acceptanceTests)
        {
            return _acceptanceTests.ToArray();
        }
    }

    private IRuleKind<TContract, TContext> KindFor(RuleOrigin origin) => origin switch
    {
        RuleOrigin.Json => _json ?? throw new RuleStateException(
            "This is a JSON rule but JSON rules are not enabled. Call EnableJsonRules(...) on the engine first."),
        _ => _csharp,
    };

    /// <summary>Implementation returned by <see cref="Resolve"/> when no rule matches.</summary>
    public void SetFallback(TContract fallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fallback = fallback;
    }

    /// <summary>
    /// Enables JSON-DSL rules by teaching the engine how to turn a document's <c>then</c> node
    /// into a contract implementation. <typeparamref name="TThen"/> is your own DTO: its shape is
    /// what a rule author (human or LLM) may write, System.Text.Json validates it, and the LLM
    /// prompt is rendered from it — so the vocabulary cannot drift from the code that reads it.
    ///
    /// Call this at startup, BEFORE <see cref="ReloadFromStore"/>, or stored JSON rules
    /// cannot be loaded.
    ///
    /// <paramref name="build"/> must be pure: it runs during validation of every candidate,
    /// including ones that are never approved, and may run concurrently.
    /// </summary>
    /// <param name="build">Turns a validated <typeparamref name="TThen"/> into the implementation to use.</param>
    /// <param name="stringComparison">
    /// How <c>eq</c>/<c>contains</c>/<c>startsWith</c>/<c>endsWith</c> compare strings.
    /// Defaults to case-insensitive: rule authors write "VIP" where data says "vip", and that
    /// mismatch would otherwise be silent.
    /// </param>
    public void EnableJsonRules<TThen>(
        Func<TThen, TContract> build,
        StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(build);
        if (_json is not null)
            throw new InvalidOperationException("JSON rules are already enabled on this engine.");

        _json = new JsonRuleKind<TContract, TContext, TThen>(build, stringComparison, _options, AcceptanceTests);
        _jsonPrompts = new RulePrompts(
            PromptBuilder.Json.SystemPrompt,
            spec => PromptBuilder.Json.BuildUserPrompt<TContext, TThen>(spec, stringComparison),
            source => JsonRuleParser.TryReadRefusal(source, out var reason) ? reason : null);

        _logger.LogInformation(
            "JSON rules enabled with 'then' shape {ThenType} and {Comparison} string comparison.",
            typeof(TThen).Name, stringComparison);
    }

    /// <summary>Developer-authored invariant run against every future candidate (the non-circular test gate).</summary>
    public void AddAcceptanceTest(IRuleAcceptanceTest<TContract> test)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_acceptanceTests)
        {
            _acceptanceTests.Add(test);
        }
    }

    // ---------------------------------------------------------------- adding rules

    /// <summary>
    /// Registers a rule implemented in host code. It is not compiled, not written to the store
    /// and not queued for approval — your repository and code review are the gate, and the code
    /// is already part of the application.
    ///
    /// Static rules compete with generated rules by priority in the usual way. They live only
    /// as long as the process, so call this at startup on every boot (unlike stored rules,
    /// which <see cref="ReloadFromStore"/> brings back).
    /// </summary>
    public RuleInfo AddStaticRule(IRule<TContract, TContext> rule, string? name = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rule);

        var entry = _registry.Add(
            $"static-{NewId()}",
            string.IsNullOrWhiteSpace(name) ? rule.GetType().Name : name,
            RuleOrigin.Static,
            rule,
            context: null);

        _logger.LogInformation(
            "Static rule {RuleId} ('{Name}') registered from host code with priority {Priority}.",
            entry.Id, entry.Name, rule.Priority);

        return ToInfo(entry, EvaluationOrders());
    }

    /// <summary>
    /// Generates a rule from a natural-language spec via the configured LLM, validates it and
    /// stores it as <see cref="RuleStatus.PendingApproval"/> (or loads it directly with AutoApprove).
    /// </summary>
    public Task<RuleInfo> AddRuleAsync(string spec, string? name = null, CancellationToken cancellationToken = default) =>
        GenerateAsync(spec, name, RuleOrigin.Compiled, cancellationToken);

    /// <summary>
    /// Generates a JSON-DSL rule from a natural-language spec, validates it and stores it as
    /// <see cref="RuleStatus.PendingApproval"/>. Requires <see cref="EnableJsonRules{TThen}"/>.
    ///
    /// Throws <see cref="RuleNotExpressibleException"/> when the spec needs something the DSL
    /// deliberately lacks (arithmetic, aggregation, the current date) — escalate it to
    /// <see cref="AddRuleAsync"/> rather than letting the model approximate it.
    /// </summary>
    public Task<RuleInfo> AddJsonRuleAsync(string spec, string? name = null, CancellationToken cancellationToken = default) =>
        GenerateAsync(spec, name, RuleOrigin.Json, cancellationToken);

    private async Task<RuleInfo> GenerateAsync(string spec, string? name, RuleOrigin origin, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_options.ChatClient is null)
            throw new InvalidOperationException(
                "RuleEngineOptions.ChatClient must be set to generate rules from natural-language specs.");

        var kind = KindFor(origin);
        var id = NewId();
        var generator = new RuleGenerator(_options.ChatClient, _options.ModelId, _options.MaxGenerationAttempts, _logger);
        var (source, outcome) = await generator
            .GenerateAsync<TContract, TContext>(id, spec, PromptsFor(origin, id), s => kind.Validate(id, s), cancellationToken)
            .ConfigureAwait(false);

        return Persist(id, name, spec, source, outcome, origin, _options.ModelId);
    }

    /// <summary>Validates hand-written C# rule source and stores it as <see cref="RuleStatus.PendingApproval"/>.</summary>
    public RuleInfo AddRuleFromSource(string source, string? name = null, string? spec = null) =>
        AddFromSource(source, name, spec, RuleOrigin.Compiled);

    /// <summary>
    /// Validates a hand-written JSON-DSL document and stores it as <see cref="RuleStatus.PendingApproval"/>.
    /// Requires <see cref="EnableJsonRules{TThen}"/>.
    /// </summary>
    public RuleInfo AddJsonRuleFromSource(string source, string? name = null, string? spec = null) =>
        AddFromSource(source, name, spec, RuleOrigin.Json);

    /// <summary>
    /// <see cref="AddRuleFromSource"/> offloaded to the thread pool, so a request thread does not
    /// block on the Roslyn compile. The token is honoured at entry (it cannot interrupt a compile
    /// already under way).
    /// </summary>
    public Task<RuleInfo> AddRuleFromSourceAsync(
        string source, string? name = null, string? spec = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => AddRuleFromSource(source, name, spec), cancellationToken);

    /// <inheritdoc cref="AddRuleFromSourceAsync"/>
    public Task<RuleInfo> AddJsonRuleFromSourceAsync(
        string source, string? name = null, string? spec = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => AddJsonRuleFromSource(source, name, spec), cancellationToken);

    private RuleInfo AddFromSource(string source, string? name, string? spec, RuleOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var kind = KindFor(origin);
        var id = NewId();
        var outcome = kind.Validate(id, source);
        if (!outcome.Success)
            throw new RuleValidationException("The rule failed validation; see Report for details.", outcome.Report);

        return Persist(id, name, spec, source, outcome, origin, modelId: null);
    }

    private RulePrompts PromptsFor(RuleOrigin origin, string ruleId) => origin == RuleOrigin.Json
        ? _jsonPrompts ?? throw new RuleStateException("JSON rules are not enabled.")
        : new RulePrompts(
            PromptBuilder.CSharp.SystemPrompt,
            spec => PromptBuilder.CSharp.BuildUserPrompt<TContract, TContext>(ruleId, spec));

    // The id is freshly minted and has not been handed out yet, so no other thread can be working
    // on it — this is the one write path that needs no per-rule lock.
    private RuleInfo Persist(
        string id, string? name, string? spec, string source,
        PipelineOutcome<TContract, TContext> outcome, RuleOrigin origin, string? modelId)
    {
        var metadata = new RuleRecord
        {
            Id = id,
            // An explicit name wins; otherwise use the one the document gave itself.
            Name = Coalesce(name, outcome.SuggestedName) ?? $"rule-{id}",
            Spec = spec,
            Origin = origin,
            Status = RuleStatus.PendingApproval,
            Priority = outcome.Priority,
            CreatedUtc = DateTimeOffset.UtcNow,
            // Hash the exact source we hand the store; the store writes it verbatim, so this stays the
            // signal the tamper check compares against.
            SourceSha256 = RuleHash.Sha256Hex(source),
            ContractType = ContractTypeName,
            ContextType = ContextTypeName,
            ContractFingerprint = CurrentFingerprint,
            ModelId = modelId,
            Report = outcome.Report,
        };
        _store.Save(new StoredRule(metadata, source));
        _logger.LogInformation("Rule {RuleId} ('{Name}') stored as PendingApproval.", id, metadata.Name);

        return _options.AutoApprove
            ? ApproveCore(metadata, approvedBy: "auto-approve", preValidated: outcome)
            : ToInfo(metadata);
    }

    // ---------------------------------------------------------------- approval workflow

    /// <summary>Validated candidates awaiting review — map this to your GET endpoint.</summary>
    public IReadOnlyList<PendingRule> GetPendingRules() =>
        OwnRules()
            .Where(m => m.Status == RuleStatus.PendingApproval)
            .Select(m => new PendingRule(
                m.Id, m.Name, m.Origin, m.Spec, _store.ReadSource(m), m.Report ?? EmptyReport, m.CreatedUtc))
            .ToList();

    /// <summary>
    /// Approves a pending rule: revalidates, loads it into the running application, marks it Approved.
    /// Recompiles the rule, so it costs the same as adding one — this is not a cheap call.
    /// </summary>
    public RuleInfo Approve(string ruleId, string approvedBy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_locks.For(ruleId))
        {
            var metadata = _store.Find(ruleId) ?? throw new RuleNotFoundException(ruleId);
            if (metadata.Status != RuleStatus.PendingApproval)
                throw new RuleStateException($"Rule '{ruleId}' is {metadata.Status}; only PendingApproval rules can be approved.");

            return ApproveCore(metadata, approvedBy, preValidated: null);
        }
    }

    /// <summary>
    /// <see cref="Approve"/> offloaded to the thread pool: approval recompiles the rule, so it is as
    /// expensive as adding one. The token is honoured at entry, not mid-compile.
    /// </summary>
    public Task<RuleInfo> ApproveAsync(string ruleId, string approvedBy, CancellationToken cancellationToken = default) =>
        Task.Run(() => Approve(ruleId, approvedBy), cancellationToken);

    /// <summary>Rejects a pending rule. It stays on disk for audit but will never load.</summary>
    public RuleInfo Reject(string ruleId, string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_locks.For(ruleId))
        {
            var metadata = _store.Find(ruleId) ?? throw new RuleNotFoundException(ruleId);
            if (metadata.Status != RuleStatus.PendingApproval)
                throw new RuleStateException($"Rule '{ruleId}' is {metadata.Status}; only PendingApproval rules can be rejected.");

            var rejected = metadata with { Status = RuleStatus.Rejected, StatusReason = reason };
            _store.Update(rejected);
            _logger.LogInformation("Rule {RuleId} rejected: {Reason}", ruleId, reason);
            return ToInfo(rejected);
        }
    }

    private RuleInfo ApproveCore(
        RuleRecord metadata, string approvedBy, PipelineOutcome<TContract, TContext>? preValidated)
    {
        // Both of these are host configuration problems rather than bad rules, so they throw before
        // anything is written to disk: a JSON rule with JSON support disabled, and a rule belonging
        // to another engine that shares this folder.
        var kind = KindFor(metadata.Origin);
        if (IsForAnotherContract(metadata))
            throw new RuleStateException(
                $"Rule '{metadata.Id}' was written against {metadata.ContractType}/{metadata.ContextType}, " +
                $"but this engine serves {ContractTypeName}/{ContextTypeName}. " +
                "Give each engine its own RuleEngineOptions.StorePath.");

        var source = _store.ReadSource(metadata);

        if (IsSourceTampered(metadata, source))
            throw new RuleStateException(
                $"Source of rule '{metadata.Id}' was modified on disk after validation; refusing to load it.");

        // Revalidate right before load: the contract may have changed since the rule
        // entered the queue, and the reviewer may have waited across an app restart.
        var outcome = preValidated ?? kind.Validate(metadata.Id, source);
        if (outcome.Load is null)
        {
            var quarantined = metadata with { Report = outcome.Report };
            Quarantine(quarantined, RevalidationFailure(metadata));
            throw new RuleValidationException(
                $"Rule '{metadata.Id}' failed revalidation and was quarantined.", outcome.Report);
        }

        var loaded = outcome.Load();
        _registry.Add(metadata.Id, metadata.Name, metadata.Origin, loaded.Rule, loaded.Context);

        var approved = metadata with
        {
            Status = RuleStatus.Approved,
            StatusReason = null,
            ApprovedBy = approvedBy,
            ApprovedAtUtc = DateTimeOffset.UtcNow,
            Priority = outcome.Priority,
            ContractType = ContractTypeName,
            ContextType = ContextTypeName,
            ContractFingerprint = CurrentFingerprint,
        };
        _store.Update(approved);
        _logger.LogInformation("Rule {RuleId} approved by {ApprovedBy} and loaded.", metadata.Id, approvedBy);

        return ToInfo(approved);
    }

    /// <summary>
    /// Brings a Disabled or Quarantined rule back: revalidates it exactly as approval does, and
    /// loads it if it passes. This is the way back from <see cref="RemoveRule"/>, and the way a rule
    /// quarantined by a contract change returns once the contract is restored — without it, the
    /// store keeps the source and the rule is still unreachable except by submitting it afresh.
    ///
    /// A Rejected rule is not eligible: rejection is a decision on the record, and reversing it
    /// should mean a new candidate going through review, not a status edit.
    /// </summary>
    public RuleInfo Enable(string ruleId, string enabledBy)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_locks.For(ruleId))
        {
            var metadata = _store.Find(ruleId) ?? throw new RuleNotFoundException(ruleId);
            if (metadata.Status is not (RuleStatus.Disabled or RuleStatus.Quarantined))
                throw new RuleStateException(
                    $"Rule '{ruleId}' is {metadata.Status}; only Disabled and Quarantined rules can be enabled." +
                    (metadata.Status == RuleStatus.Rejected
                        ? " A rejected rule stays rejected — submit it again as a new candidate."
                        : string.Empty));

            return ApproveCore(metadata, enabledBy, preValidated: null);
        }
    }

    /// <summary><see cref="Enable"/> offloaded to the thread pool; like approval, it recompiles the rule.</summary>
    public Task<RuleInfo> EnableAsync(string ruleId, string enabledBy, CancellationToken cancellationToken = default) =>
        Task.Run(() => Enable(ruleId, enabledBy), cancellationToken);

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Unregisters the rule (no new resolutions see it) and requests unload of its load
    /// context. The unload is cooperative: the assembly disappears only once nothing
    /// references it anymore, so in-flight calls finish safely.
    /// </summary>
    public void RemoveRule(string ruleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_locks.For(ruleId))
        {
            var removed = _registry.Remove(ruleId);
            removed?.Context?.Unload();

            var metadata = _store.Find(ruleId);
            if (metadata is null && removed is null)
                throw new RuleNotFoundException(ruleId);

            if (metadata is not null)
                _store.Update(metadata with { Status = RuleStatus.Disabled });

            _logger.LogInformation("Rule {RuleId} removed (was loaded: {WasLoaded}).", ruleId, removed is not null);
        }
    }

    /// <summary>
    /// Recompiles (or reparses) and loads every Approved rule from the store — call once at
    /// startup, after <see cref="EnableJsonRules{TThen}"/> if you use JSON rules. Rules that no
    /// longer pass validation — e.g. after a contract change — are quarantined instead of
    /// crashing the application.
    /// </summary>
    public void ReloadFromStore(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var metadata in _store.LoadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_locks.For(metadata.Id))
            {
                if (metadata.Status != RuleStatus.Approved || _registry.Contains(metadata.Id))
                    continue;

                // Same principle as the JSON check below, and the reason StorePath's default is a
                // trap worth guarding: this rule is another engine's, and its source cannot be
                // expected to compile against our contract. Quarantining it would be this engine
                // vandalizing rules it does not own.
                if (IsForAnotherContract(metadata))
                {
                    _logger.LogError(
                        "Rule {RuleId} ('{Name}') was written against {StoredContract}/{StoredContext} but this " +
                        "engine serves {Contract}/{Context}; skipping it. Two engines are sharing a StorePath — " +
                        "give each its own.",
                        metadata.Id, metadata.Name, metadata.ContractType, metadata.ContextType,
                        ContractTypeName, ContextTypeName);
                    continue;
                }

                // A JSON rule with JSON support disabled means the HOST is misconfigured, not that
                // the rule is bad. Quarantine is a permanent disk write that fixing the host would
                // not undo, so skip loudly and leave the rule Approved.
                if (metadata.Origin == RuleOrigin.Json && _json is null)
                {
                    _logger.LogError(
                        "Rule {RuleId} ('{Name}') is a JSON rule but JSON rules are not enabled; skipping it. " +
                        "Call EnableJsonRules(...) before ReloadFromStore().",
                        metadata.Id, metadata.Name);
                    continue;
                }

                var source = _store.ReadSource(metadata);
                if (IsSourceTampered(metadata, source))
                {
                    Quarantine(metadata, "Source file on disk does not match the stored hash (tampered?).");
                    continue;
                }

                var outcome = KindFor(metadata.Origin).Validate(metadata.Id, source);
                if (outcome.Load is null)
                {
                    Quarantine(metadata with { Report = outcome.Report }, RevalidationFailure(metadata));
                    continue;
                }

                var loaded = outcome.Load();
                _registry.Add(metadata.Id, metadata.Name, metadata.Origin, loaded.Rule, loaded.Context);
                _logger.LogInformation("Rule {RuleId} ('{Name}') reloaded from store.", metadata.Id, metadata.Name);
            }
        }
    }

    /// <summary>
    /// Unregisters every rule and requests unload of the assemblies this engine loaded. The unload
    /// stays cooperative, so an implementation the host is still holding keeps its assembly alive
    /// until it is dropped. Static rules and the fallback are the host's own objects and are left
    /// alone; nothing is written to the store, so a new engine over the same folder reloads it all.
    ///
    /// Chiefly for hosts that create engines per scope — a test suite, say. A singleton engine
    /// normally lives as long as the process.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var entry in _registry.Clear())
            entry.Context?.Unload();
    }

    private void Quarantine(RuleRecord metadata, string reason)
    {
        _store.Update(metadata with { Status = RuleStatus.Quarantined, StatusReason = reason });
        _logger.LogWarning("Rule {RuleId} quarantined: {Reason}", metadata.Id, reason);
    }

    // Integrity lives in the engine, not the store, so a custom IRuleStore stays pure persistence:
    // compare the hash of what is on disk now to the one recorded when the source was saved.
    private static bool IsSourceTampered(RuleRecord record, string source) =>
        record.SourceSha256 is not null && RuleHash.Sha256Hex(source) != record.SourceSha256;

    // ---------------------------------------------------------------- dispatch

    /// <summary>Returns the implementation of the first rule in evaluation order that matches, the fallback, or null.</summary>
    /// <remarks>
    /// Trust boundary: the engine guards its OWN calls into a rule — <c>AppliesTo</c> and
    /// <c>Priority</c> run under try/catch here — but the implementation this returns is handed
    /// straight back to you, and the method you then invoke on it runs on your thread with no
    /// timeout, no exception isolation and no memory bound. For a compiled (LLM-generated) rule that
    /// is unsandboxed code: a pathological branch can loop forever or throw. The real defense is the
    /// acceptance tests and the human approval gate, not this call — a rule you would not hand a
    /// production request to should not be Approved. Wrap the invocation yourself if a request thread
    /// must never block on it.
    /// </remarks>
    public TContract? Resolve(TContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var matches = Match(context);
        if (matches.Count == 0)
            return _fallback;

        if (_options.ResolutionPolicy == ResolutionPolicy.ThrowOnAmbiguity && matches.Count > 1)
            throw new AmbiguousRuleMatchException(matches.Select(m => m.Id).ToList());

        return InEvaluationOrder(matches).First().Rule.Implementation;
    }

    /// <summary>All matching implementations in evaluation order (fallback not included).</summary>
    public IReadOnlyList<TContract> ResolveAll(TContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return InEvaluationOrder(Match(context)).Select(r => r.Rule.Implementation).ToList();
    }

    /// <summary>
    /// The order in which rules are consulted, per the configured <see cref="ResolutionPolicy"/>.
    /// The single source of truth behind Resolve, ResolveAll and the order reported by GetRules.
    /// </summary>
    private IEnumerable<ActiveRule<TContract, TContext>> InEvaluationOrder(
        IEnumerable<ActiveRule<TContract, TContext>> rules) =>
        _options.ResolutionPolicy switch
        {
            // Registration order: oldest rule first.
            ResolutionPolicy.FirstMatch or ResolutionPolicy.ThrowOnAmbiguity =>
                rules.OrderBy(r => r.Sequence),

            // HighestPriority (default): highest priority first, newest wins ties.
            _ => rules
                .OrderByDescending(r => SafePriority(r))
                .ThenByDescending(r => r.Sequence),
        };

    private int SafePriority(ActiveRule<TContract, TContext> entry)
    {
        try
        {
            return entry.Rule.Priority;
        }
        catch (Exception ex)
        {
            if (entry.PriorityFailures.ShouldLog(out var count))
                _logger.LogWarning(
                    ex, "Priority of rule {RuleId} threw; treating it as 0 ({Count} occurrence(s) so far).",
                    entry.Id, count);
            return 0;
        }
    }

    private List<ActiveRule<TContract, TContext>> Match(TContext context)
    {
        var matches = new List<ActiveRule<TContract, TContext>>();
        foreach (var entry in _registry.Snapshot)
        {
            try
            {
                if (entry.Rule.AppliesTo(context))
                    matches.Add(entry);
            }
            catch (Exception ex)
            {
                if (entry.PredicateFailures.ShouldLog(out var count))
                    _logger.LogWarning(
                        ex, "Predicate of rule {RuleId} threw and the rule was skipped ({Count} occurrence(s) so far).",
                        entry.Id, count);
            }
        }

        return matches;
    }

    // ---------------------------------------------------------------- introspection

    /// <summary>
    /// One rule by id — a stored rule of any status (unless another engine left it in the folder), or
    /// a static rule; null when there is no such rule. A single store lookup, so cheaper than
    /// filtering <see cref="GetRules"/> when you only want one — map it to a details endpoint.
    /// </summary>
    public RuleInfo? GetRule(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var orders = EvaluationOrders();

        var metadata = _store.Find(id);
        if (metadata is not null && !IsForAnotherContract(metadata))
            return ToInfo(metadata, orders);

        var entry = _registry.Snapshot.FirstOrDefault(e => e.Id == id && e.Origin == RuleOrigin.Static);
        return entry is not null ? ToInfo(entry, orders) : null;
    }

    /// <summary>
    /// Every rule the engine knows about — stored ones (any status) and static ones — with the
    /// position each loaded rule occupies in the evaluation order. Loaded rules come first, in
    /// the exact order their predicates are consulted; everything else follows by creation time
    /// with a null <see cref="RuleInfo.EvaluationOrder"/>.
    /// </summary>
    public IReadOnlyList<RuleInfo> GetRules()
    {
        var orders = EvaluationOrders();

        var infos = OwnRules().Select(m => ToInfo(m, orders)).ToList();
        infos.AddRange(_registry.Snapshot
            .Where(entry => entry.Origin == RuleOrigin.Static)
            .Select(entry => ToInfo(entry, orders)));

        return infos
            .OrderBy(info => info.EvaluationOrder ?? int.MaxValue)
            .ThenBy(info => info.CreatedUtc)
            .ToList();
    }

    /// <summary>
    /// The store's rules minus any another engine left in the same folder. An engine reports on the
    /// rules it could actually run — listing a rule it would refuse to approve helps nobody.
    /// </summary>
    private IEnumerable<RuleRecord> OwnRules() => _store.LoadAll().Where(m => !IsForAnotherContract(m));

    /// <summary>Loaded rule id → 0-based position in the current evaluation order.</summary>
    private Dictionary<string, int> EvaluationOrders()
    {
        var orders = new Dictionary<string, int>();
        var position = 0;
        foreach (var entry in InEvaluationOrder(_registry.Snapshot))
            orders[entry.Id] = position++;
        return orders;
    }

    private RuleInfo ToInfo(RuleRecord metadata, Dictionary<string, int>? orders = null) => new(
        metadata.Id,
        metadata.Name,
        metadata.Status,
        metadata.Origin,
        metadata.Priority,
        metadata.Spec,
        metadata.CreatedUtc,
        metadata.ApprovedBy,
        metadata.ApprovedAtUtc,
        metadata.StatusReason,
        IsLoaded: _registry.Contains(metadata.Id),
        EvaluationOrder: Order(orders, metadata.Id));

    // Static rules have no store record: they are live by definition, approved by code review.
    private RuleInfo ToInfo(ActiveRule<TContract, TContext> entry, Dictionary<string, int>? orders = null) => new(
        entry.Id,
        entry.Name,
        RuleStatus.Approved,
        RuleOrigin.Static,
        SafePriority(entry),
        Spec: null,
        entry.CreatedUtc,
        ApprovedBy: null,
        ApprovedAtUtc: null,
        StatusReason: null,
        IsLoaded: true,
        EvaluationOrder: Order(orders, entry.Id));

    private static int? Order(Dictionary<string, int>? orders, string id) =>
        orders is not null && orders.TryGetValue(id, out var order) ? order : null;

    private static readonly ValidationReport EmptyReport = new([], [], []);

    // Full 128 bits, not a truncated slice: the id names the store files a rule is saved under, so a
    // collision would silently overwrite another rule. 48 bits (12 hex) reached a 1% birthday chance
    // around a few million rules; the full GUID makes that a non-consideration.
    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string? Coalesce(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
