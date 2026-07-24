using DuetControlServer.Commands;
using DuetControlServer.IPC.Processors;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace UnitTests.IPC;

[TestFixture]
public class SupportedCommands
{
    [Test]
    public void DescriptorMatchesCommandType()
    {
        SupportedCommand command = SupportedCommand.For<SimpleCode>();
        Assert.That(command.Name, Is.EqualTo(nameof(SimpleCode)));
        Assert.That(command.Type, Is.EqualTo(typeof(SimpleCode)));
    }

    [Test]
    public void IsSupported()
    {
        Assert.That(SupportedCommand.IsSupported(Command.SupportedCommands, typeof(SimpleCode)), Is.True);
        Assert.That(SupportedCommand.IsSupported(Command.SupportedCommands, typeof(Code)), Is.True);
        Assert.That(SupportedCommand.IsSupported(Command.SupportedCommands, typeof(Flush)), Is.True);
        Assert.That(SupportedCommand.IsSupported(Command.SupportedCommands, typeof(string)), Is.False);
        Assert.That(SupportedCommand.IsSupported([], typeof(SimpleCode)), Is.False);
    }

    [Test]
    public void CommandNamesAreUnique()
    {
        // Commands are dispatched by name, so a duplicate would silently shadow another command
        foreach (SupportedCommand[] commands in new[] { Command.SupportedCommands, CodeInterception.AllSupportedCommands, CodeStream.SupportedCommands, ModelSubscription.SupportedCommands })
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (SupportedCommand command in commands)
            {
                Assert.That(names.Add(command.Name), Is.True, $"Duplicate command name {command.Name}");
            }
        }
    }
}
