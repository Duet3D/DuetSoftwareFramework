using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace SystemTests.Host;

/// <summary>
/// The G-, M- or T-codes whose behaviour a scenario or fixture specifies, added as test categories
/// so that every scenario of a code can be run however the files are laid out:
/// <c>scripts/test.py system --filter 'TestCategory=M140'</c>
/// </summary>
/// <remarks>
/// Only for the codes a scenario is about. A scenario that sends G1 to get the machine somewhere,
/// or M32 to start the job it is really testing, does not name them
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed partial class GCodeAttribute : NUnitAttribute, IApplyToTest
{
    /// <summary>The codes, as their letter and number, e.g. M140 or M569.5</summary>
    public string[] Codes { get; }

    public GCodeAttribute(params string[] codes)
    {
        foreach (string code in codes)
        {
            if (!CodeName().IsMatch(code))
            {
                throw new ArgumentException($"\"{code}\" is not a code name such as G1, M569.5 or T", nameof(codes));
            }
        }
        Codes = codes;
    }

    public void ApplyToTest(Test test)
    {
        foreach (string code in Codes)
        {
            test.Properties.Add(PropertyNames.Category, code);
        }
    }

    [GeneratedRegex(@"^([GM]\d+(\.\d+)?|T)$")]
    private static partial Regex CodeName();
}
