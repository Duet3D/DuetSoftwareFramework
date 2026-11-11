using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace SystemTests.Host;

/// <summary>
/// Base of every bench fixture: starts each test with an empty captured log and prints it when the
/// test failed, so the DuetControlServer log rides with the failure instead of with every run
/// </summary>
public abstract class BenchFixture
{
    [SetUp]
    public void ClearCapturedLog() => CapturedLog.Clear();

    [TearDown]
    public void DumpCapturedLogOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
        {
            TestContext.Out.WriteLine("DuetControlServer log of the failed test:");
            TestContext.Out.WriteLine(CapturedLog.Dump());
        }
    }
}
