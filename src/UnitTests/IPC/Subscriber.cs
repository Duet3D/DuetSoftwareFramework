using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Collections.Generic;

namespace UnitTests.IPC
{
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

            ClassicAssert.AreEqual(1, root.Count);
            if (root.TryGetValue("state", out object? stateObject))
            {
                if (stateObject is Dictionary<string, object?> state)
                {
                    ClassicAssert.AreEqual(0, state.Count);
                    ClassicAssert.AreSame(state, resultA);
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

            ClassicAssert.AreEqual(2, root.Count);
            if (root.TryGetValue("boards", out object? boardsObject))
            {
                if (boardsObject is List<object?> boards)
                {
                    ClassicAssert.AreEqual(2, boards.Count);
                    if (boards[0] is Dictionary<string, object?> boardA)
                    {
                        ClassicAssert.AreEqual(1, boardA.Count);
                        if (boardA.TryGetValue("v12", out object? v12Object))
                        {
                            if (v12Object is Dictionary<string, object?> v12)
                            {
                                ClassicAssert.AreEqual(0, v12.Count);
                                ClassicAssert.AreSame(v12, resultB);
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
                        ClassicAssert.AreEqual(boardB.Count, 0);
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

            ClassicAssert.AreEqual(3, root.Count);
            if (root.TryGetValue("move", out object? moveObject))
            {
                if (moveObject is Dictionary<string, object> move)
                {
                    ClassicAssert.AreEqual(1, move.Count);
                    if (move.TryGetValue("axes", out object? axesObject))
                    {
                        if (axesObject is List<object?> axes)
                        {
                            ClassicAssert.AreEqual(3, axes.Count);
                            for (int i = 0; i < 2; i++)
                            {
                                if (axes[i] is Dictionary<string, object?> axis)
                                {
                                    ClassicAssert.AreEqual(0, axis.Count);
                                    if (i == 0)
                                    {
                                        ClassicAssert.AreSame(axis, resultC);
                                    }
                                    else
                                    {
                                        ClassicAssert.AreNotSame(axis, resultC);
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

            ClassicAssert.AreEqual(4, root.Count);
            if (root.TryGetValue("tools", out object? toolsObject))
            {
                if (toolsObject is List<object> tools)
                {
                    ClassicAssert.AreEqual(1, tools.Count);
                    if (tools[0] is Dictionary<string, object?> tool)
                    {
                        if (tool.TryGetValue("retraction", out object? retractionObject))
                        {
                            if (retractionObject is Dictionary<string, object?> retraction)
                            {
                                ClassicAssert.AreSame(retraction, resultD);
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
}
