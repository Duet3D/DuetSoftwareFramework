using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DuetControlServer.Link.Protocol.CanMessages;
using DuetControlServer.Link.Protocol.Shared;
using NUnit.Framework;

namespace UnitTests.Heaters;

[TestFixture]
public class FopDt
{
    /// <summary>Members of the heater model messages that do not describe the model</summary>
    private static readonly HashSet<string> NotModel =
    [
        nameof(CanMessageHeaterModelV3.RequestId),
        nameof(CanMessageHeaterModelV3.Zero),
        nameof(CanMessageHeaterModelV3.Zero2),
        nameof(CanMessageHeaterModelV3.Heater),
        nameof(CanMessageHeaterModelV3.ObsoleteWasKP),
        nameof(CanMessageHeaterModelV3.ObsoleteWasRecipTi),
        nameof(CanMessageHeaterModelV3.ObsoleteWasTD),
        nameof(CanMessageHeaterModelV3.ObsoleteWasPidParametersOverridden),
        nameof(DuetControlServer.Link.Protocol.CanMessages.HeaterModel.Fzero),
        nameof(DuetControlServer.Link.Protocol.CanMessages.HeaterModel.Zero),

        // Taken from the default model of the heater's function, see DefaultsComeFromTheHeaterFunction
        nameof(DuetControlServer.Link.Protocol.CanMessages.HeaterModel.TemperatureCoefficient),
        nameof(DuetControlServer.Link.Protocol.CanMessages.HeaterModel.TypicalTemperature),
    ];

    /// <summary>The writable model members of a generated struct, fields and bitfield properties alike</summary>
    private static IEnumerable<MemberInfo> ModelMembers(Type type)
        => type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
               .Where(m => m is FieldInfo or PropertyInfo { CanWrite: true } && !NotModel.Contains(m.Name));

    private static Type TypeOf(MemberInfo member) => member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;

    private static object? Get(MemberInfo member, object target)
        => member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)member).GetValue(target);

    private static void Set(MemberInfo member, object target, object value)
    {
        if (member is FieldInfo f)
        {
            f.SetValue(target, value);
        }
        else
        {
            ((PropertyInfo)member).SetValue(target, value);
        }
    }

    /// <summary>
    /// Fill every model member with a value of its own, nested structs included, so that two members swapped or
    /// one left out cannot read back the same
    /// </summary>
    private static object Fill(Type type, bool flag, ref float next)
    {
        object target = Activator.CreateInstance(type)!;
        foreach (MemberInfo member in ModelMembers(type))
        {
            Type memberType = TypeOf(member);
            if (memberType == typeof(float))
            {
                Set(member, target, next += 1.25f);
            }
            else if (memberType == typeof(bool))
            {
                Set(member, target, flag);
            }
            else if (memberType.IsValueType && !memberType.IsPrimitive && !memberType.IsEnum)
            {
                Set(member, target, Fill(memberType, flag, ref next));
            }
            else
            {
                Assert.Fail($"{type.Name}.{member.Name} is a {memberType.Name}, which this test cannot fill yet");
            }
        }
        return target;
    }

    private static void AssertSame(Type type, object expected, object actual, string path)
    {
        foreach (MemberInfo member in ModelMembers(type))
        {
            Type memberType = TypeOf(member);
            if (memberType.IsValueType && !memberType.IsPrimitive && !memberType.IsEnum)
            {
                AssertSame(memberType, Get(member, expected)!, Get(member, actual)!, $"{path}.{member.Name}");
            }
            else
            {
                Assert.That(Get(member, actual), Is.EqualTo(Get(member, expected)),
                            $"{path}.{member.Name} does not survive the object model; map it in FopDt.ToCanMessage and FopDt.Apply");
            }
        }
    }

    /// <summary>
    /// Every member of the heater model message survives a round trip through the object model, so a field
    /// added to the CAN schema fails here until the object model carries it
    /// </summary>
    [TestCase(true)]
    [TestCase(false)]
    public void EveryModelFieldRoundTripsThroughTheObjectModel(bool flag)
    {
        float next = 0.0f;
        CanMessageHeaterModelV3 sent = (CanMessageHeaterModelV3)Fill(typeof(CanMessageHeaterModelV3), flag, ref next);
        sent.Heater = 7;

        DuetAPI.ObjectModel.HeaterModel model = new();
        DuetControlServer.Heat.FopDt.Apply(sent, model, DuetControlServer.Heat.FopDt.SetModelPidTemperature);
        CanMessageHeaterModelV3 rebuilt = DuetControlServer.Heat.FopDt.ToCanMessage(sent.Heater, HeaterFunction.Tool, model);

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.Heater, Is.EqualTo(sent.Heater), "heater number");
            AssertSame(typeof(CanMessageHeaterModelV3), sent, rebuilt, nameof(CanMessageHeaterModelV3));
        });
    }

    /// <summary>
    /// The fields the object model does not keep are those of CANlib's default model for the heater's function
    /// </summary>
    [TestCase(HeaterFunction.Tool, 220.0f)]
    [TestCase(HeaterFunction.Bed, 60.0f)]
    [TestCase(HeaterFunction.Chamber, 60.0f)]
    public void DefaultsComeFromTheHeaterFunction(HeaterFunction function, float typicalTemperature)
    {
        CanMessageHeaterModelV3 message = DuetControlServer.Heat.FopDt.ToCanMessage(0, function, new DuetAPI.ObjectModel.HeaterModel());
        Assert.Multiple(() =>
        {
            Assert.That(message.BasicModel.TypicalTemperature, Is.EqualTo(typicalTemperature), "typicalTemperature");
            Assert.That(message.BasicModel.TemperatureCoefficient, Is.EqualTo(0.0f), "temperatureCoefficient");
        });
    }
}
