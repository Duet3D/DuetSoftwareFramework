using DuetEmulation;
using NUnit.Framework;

namespace EmulationTests;

/// <summary>
/// Behaviour a stack does not have yet, which a scenario run on that stack skips, naming the gap, rather
/// than failing. Scenarios run on every stack from one fixture, so the gap is declared where the
/// behaviour is used rather than as a category of the whole test
/// </summary>
internal static class KnownGap
{
    /// <summary>Skip the test when it runs on <paramref name="lacking"/>, which does not have the behaviour yet</summary>
    public static void On(Stack stack, Stack lacking, string gap)
    {
        if (stack == lacking)
        {
            Assert.Ignore($"Known gap on {lacking}: {gap}");
        }
    }
}
