using System;
using System.Collections.Generic;
using DuetAPI.Commands;
using NUnit.Framework;

namespace UnitTests.Commands;

/// <summary>
/// Checks on <see cref="CodeFlags"/> itself rather than on any code that uses it
/// </summary>
/// <remarks>
/// Two flags sharing a value are indistinguishable at runtime: setting either sets both, and every
/// <c>HasFlag</c> for one silently answers for the other. Writing the values as shifts makes that
/// hard to do by accident, and this makes it impossible
/// </remarks>
[TestFixture]
public class CodeFlagsTests
{
    [Test]
    public void EveryFlagHasItsOwnBit()
    {
        Dictionary<int, string> byValue = [];
        foreach (string name in Enum.GetNames<CodeFlags>())
        {
            int value = (int)Enum.Parse<CodeFlags>(name);
            if (value == 0)
            {
                continue;                   // None is the absence of every flag, not one of them
            }

            Assert.That(value & (value - 1), Is.Zero, $"{name} is not a single bit");
            Assert.That(byValue.ContainsKey(value), Is.False,
                        $"{name} has the same value as {byValue.GetValueOrDefault(value)}");
            byValue[value] = name;
        }
    }
}
