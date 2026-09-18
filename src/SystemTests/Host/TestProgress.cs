using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: SystemTests.Host.TestProgress]

namespace SystemTests.Host;

/// <summary>
/// Names each test on the console as it starts, with its position in the run and how long the run
/// has been going.
/// </summary>
/// <remarks>
/// The scenarios start a real DuetControlServer per test and several of them wait on timeouts
/// measured in tens of seconds, so a run that has stopped making progress looks exactly like a run
/// that is merely slow. The starting line tells the two apart: whatever it named last is the test
/// that is stuck.
///
/// <para>
/// Nothing references this class. NUnit applies it to every test in the assembly through the
/// <c>[assembly: TestProgress]</c> attribute at the top of this file, so a fixture added later is
/// covered without having to derive from anything.
/// </para>
///
/// <para>
/// The lines go to <see cref="TestContext.Progress"/>, which the runner streams as it is written,
/// unlike <c>TestContext.Out</c> which it holds back until the test finishes. Reaching the terminal
/// also needs the console logger at normal verbosity or above, which scripts/system-tests.sh asks
/// for. That verbosity also prints the number of tests the run discovered, which is where the count
/// each line counts towards comes from: the assembly knows how many tests it holds but not how many
/// the filter selected.
/// </para>
///
/// <para>
/// Failures are not summarised here. The runner prints each one where it happened, followed by the
/// stack trace and the DuetControlServer log <see cref="BenchFixture"/> dumps, and anything written
/// from inside the run lands above all of that rather than at the end. scripts/system-tests.sh
/// lists the failed names after the runner has finished instead.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestProgressAttribute : Attribute, ITestAction
{
    /// <summary>Wall clock for the whole run, started when the assembly suite begins.</summary>
    private static readonly Stopwatch _elapsed = new();

    /// <summary>Number of tests started so far, which is the number in the starting line.</summary>
    private static int _started;

    /// <inheritdoc/>
    /// <remarks>Suites as well as test cases: the assembly suite is where the run clock starts.</remarks>
    public ActionTargets Targets => ActionTargets.Test | ActionTargets.Suite;

    /// <inheritdoc/>
    public void BeforeTest(ITest test)
    {
        if (test.IsSuite)
        {
            // The assembly is the outermost suite, and so the only test in the tree with no parent
            if (test.Parent is null)
            {
                _elapsed.Restart();
            }
            return;
        }

        int index = Interlocked.Increment(ref _started);
        TestContext.Progress.WriteLine($"[{index,4}  {_elapsed.Elapsed:mm\\:ss}] {test.FullName}");
    }

    /// <inheritdoc/>
    public void AfterTest(ITest test)
    {
        // The runner reports the outcome of each test itself
    }
}
