using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
using System.Collections.Generic;

namespace UnitTests.IPC;

[TestFixture]
public class Subscriber
{
    [Test]
    public void GetPathNode()
    {
        Dictionary<string, object?> root = [];

        // state.status
        object[] pathA = ["state", "status"];
        object? resultA = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathA);

        Assert.That(root.Count, Is.EqualTo(1));
        if (root.TryGetValue("state", out object? stateObject))
        {
            if (stateObject is Dictionary<string, object?> state)
            {
                Assert.That(state.Count, Is.EqualTo(0));
                Assert.That(resultA, Is.SameAs(state));
            }
            else
            {
                Assert.Fail("Invalid state type");
            }
        }
        else
        {
            Assert.Fail("Missing state");
        }

        // boards[0 of 2]/v12/current
        object[] pathB = [new ItemPathNode("boards", 0, new object[] { new Board(), new Board() }), "v12", "current"];
        object? resultB = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathB);

        Assert.That(root.Count, Is.EqualTo(2));
        if (root.TryGetValue("boards", out object? boardsObject))
        {
            if (boardsObject is List<object> boards)
            {
                Assert.That(boards.Count, Is.EqualTo(2));
                if (boards[0] is Dictionary<string, object?> boardA)
                {
                    Assert.That(boardA.Count, Is.EqualTo(1));
                    if (boardA.TryGetValue("v12", out object? v12Object))
                    {
                        if (v12Object is Dictionary<string, object?> v12)
                        {
                            Assert.That(v12.Count, Is.EqualTo(0));
                            Assert.That(resultB, Is.SameAs(v12));
                        }
                        else
                        {
                            Assert.Fail("Invalid board[0].v12 type");
                        }
                    }
                    else
                    {
                        Assert.Fail("Missing boards[0].v12");
                    }
                }
                else
                {
                    Assert.Fail("Invalid board[0] type");
                }

                if (boards[1] is Dictionary<string, object?> boardB)
                {
                    Assert.That(boardB.Count, Is.EqualTo(0));
                }
                else
                {
                    Assert.Fail("Invalid board[1] type");
                }
            }
        }
        else
        {
            Assert.Fail("Missing boards");
        }

        // move.axes[0 of 2].homed
        object[] pathC = ["move", new ItemPathNode("axes", 0, new object[] { new Axis(), new Axis(), new Axis() }), "homed"];
        object? resultC = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathC);

        Assert.That(root.Count, Is.EqualTo(3));
        if (root.TryGetValue("move", out object? moveObject))
        {
            if (moveObject is Dictionary<string, object?> move)
            {
                Assert.That(move.Count, Is.EqualTo(1));
                if (move.TryGetValue("axes", out object? axesObject))
                {
                    if (axesObject is List<object> axes)
                    {
                        Assert.That(axes.Count, Is.EqualTo(3));
                        for (int i = 0; i < 2; i++)
                        {
                            if (axes[i] is Dictionary<string, object?> axis)
                            {
                                Assert.That(axis.Count, Is.EqualTo(0));
                                if (i == 0)
                                {
                                    Assert.That(resultC, Is.SameAs(axis));
                                }
                                else
                                {
                                    Assert.That(resultC, Is.Not.SameAs(axis));
                                }
                            }
                            else
                            {
                                Assert.Fail($"Invalid move.axes[{i}] type");
                            }
                        }
                    }
                    else
                    {
                        Assert.Fail("Invalid move.axes type");
                    }
                }
                else
                {
                    Assert.Fail("Missing move.axes");
                }
            }
            else
            {
                Assert.Fail("Invalid move type");
            }
        }
        else
        {
            Assert.Fail("Missing move");
        }

        // tools[0 of 1]/retraction/length
        object[] pathD = [new ItemPathNode("tools", 0, new object[] { new Tool() }), "retraction", "length"];
        object? resultD = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathD);

        Assert.That(root.Count, Is.EqualTo(4));
        if (root.TryGetValue("tools", out object? toolsObject))
        {
            if (toolsObject is List<object> tools)
            {
                Assert.That(tools.Count, Is.EqualTo(1));
                if (tools[0] is Dictionary<string, object?> tool)
                {
                    if (tool.TryGetValue("retraction", out object? retractionObject))
                    {
                        if (retractionObject is Dictionary<string, object?> retraction)
                        {
                            Assert.That(resultD, Is.SameAs(retraction));
                        }
                        else
                        {
                            Assert.Fail("Invalid tools[0].retraction type");
                        }
                    }
                    else
                    {
                        Assert.Fail("Missing tools[0].retraction");
                    }
                }
                else
                {
                    Assert.Fail("Invalid tools[0] type");
                }
            }
            else
            {
                Assert.Fail("Invalid tools type");
            }
        }
        else
        {
            Assert.Fail("Missing tools");
        }
    }
}
