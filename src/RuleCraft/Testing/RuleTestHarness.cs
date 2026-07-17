using RuleCraft.Loading;

namespace RuleCraft.Testing;

/// <summary>
/// Executes a candidate's generated <see cref="IRuleTest"/>s and the developer's
/// <see cref="IRuleAcceptanceTest{TContract}"/>s inside a throwaway collectible load
/// context, then unloads it. In-process execution cannot stop a runaway CPU loop -
/// a timed-out test is reported as failed and its thread may leak; the security
/// analyzer removes most other ways for a test to hang.
/// </summary>
internal static class RuleTestHarness
{
    public static (IReadOnlyList<TestCaseResult> Results, int Priority) Run<TContract, TContext>(
        byte[] assemblyBytes,
        string ruleId,
        IReadOnlyList<IRuleAcceptanceTest<TContract>> acceptanceTests,
        TimeSpan timeout)
        where TContract : class
    {
        var results = new List<TestCaseResult>();
        var priority = 0;
        var context = new RuleAssemblyLoadContext($"RuleCraft:candidate:{ruleId}");
        try
        {
            using var stream = new MemoryStream(assemblyBytes);
            var assembly = context.LoadFromStream(stream);

            var ruleTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(IRule<TContract, TContext>).IsAssignableFrom(t))
                .ToArray();

            if (ruleTypes.Length != 1)
            {
                results.Add(new TestCaseResult(
                    "harness:rule-shape",
                    TestOutcome.Failed,
                    $"Expected exactly one IRule<{typeof(TContract).Name}, {typeof(TContext).Name}> implementation, found {ruleTypes.Length}."));
                return (results, priority);
            }

            IRule<TContract, TContext> rule;
            try
            {
                rule = (IRule<TContract, TContext>)Activator.CreateInstance(ruleTypes[0])!;
            }
            catch (Exception ex)
            {
                results.Add(new TestCaseResult(
                    "harness:rule-instantiation", TestOutcome.Failed,
                    $"Could not instantiate rule (a public parameterless constructor is required): {Unwrap(ex).Message}"));
                return (results, priority);
            }

            priority = rule.Priority;

            foreach (var testType in assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IRuleTest).IsAssignableFrom(t)))
            {
                IRuleTest test;
                try
                {
                    test = (IRuleTest)Activator.CreateInstance(testType)!;
                }
                catch (Exception ex)
                {
                    results.Add(new TestCaseResult(testType.Name, TestOutcome.Failed,
                        $"Could not instantiate test: {Unwrap(ex).Message}"));
                    continue;
                }

                results.Add(RunWithTimeout(test.Name, timeout, ct => test.Run(new TestContext(ct))));
            }

            var implementation = rule.Implementation;
            foreach (var acceptance in acceptanceTests)
            {
                results.Add(RunWithTimeout($"acceptance:{acceptance.Name}", timeout, _ => acceptance.Run(implementation)));
            }

            return (results, priority);
        }
        catch (Exception ex)
        {
            results.Add(new TestCaseResult("harness:load", TestOutcome.Failed, Unwrap(ex).Message));
            return (results, priority);
        }
        finally
        {
            context.Unload();
        }
    }

    private static TestCaseResult RunWithTimeout(string name, TimeSpan timeout, Func<CancellationToken, TestResult> body) =>
        TestExecution.RunWithTimeout(name, timeout, body);

    private static Exception Unwrap(Exception ex) => TestExecution.Unwrap(ex);
}
