using DuetAPI.ObjectModel;
using DuetControlServer.Model;
using NUnit.Framework;
using System.Collections.Generic;

namespace UnitTests.IPC
{
    [TestFixture]
    public class Subscriber
    {
        [Test]
        public void GetPathNode()
        {
            Dictionary<string, object?> root = new();

            // state.status
            object[] pathA = new object[] { "state", "status" };
            object? resultA = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathA);

            Assert.AreEqual(1, root.Count);
            if (root.TryGetValue("state", out object? stateObject))
            {
                if (stateObject is Dictionary<string, object?> state)
                {
                    Assert.AreEqual(0, state.Count);
                    Assert.AreSame(state, resultA);
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
            object[] pathB = new object[] { new ItemPathNode("boards", 0, new object[] { new Board(), new Board() }), "v12", "current" };
            object? resultB = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathB);

            Assert.AreEqual(2, root.Count);
            if (root.TryGetValue("boards", out object? boardsObject))
            {
                if (boardsObject is List<object?> boards)
                {
                    Assert.AreEqual(2, boards.Count);
                    if (boards[0] is Dictionary<string, object?> boardA)
                    {
                        Assert.AreEqual(1, boardA.Count);
                        if (boardA.TryGetValue("v12", out object? v12Object))
                        {
                            if (v12Object is Dictionary<string, object?> v12)
                            {
                                Assert.AreEqual(0, v12.Count);
                                Assert.AreSame(v12, resultB);
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
                        Assert.AreEqual(boardB.Count, 0);
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
            object[] pathC = new object[] { "move", new ItemPathNode("axes", 0, new object[] { new Axis(), new Axis(), new Axis() }), "homed" };
            object? resultC = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathC);

            Assert.AreEqual(3, root.Count);
            if (root.TryGetValue("move", out object? moveObject))
            {
                if (moveObject is Dictionary<string, object> move)
                {
                    Assert.AreEqual(1, move.Count);
                    if (move.TryGetValue("axes", out object? axesObject))
                    {
                        if (axesObject is List<object?> axes)
                        {
                            Assert.AreEqual(3, axes.Count);
                            for (int i = 0; i < 2; i++)
                            {
                                if (axes[i] is Dictionary<string, object?> axis)
                                {
                                    Assert.AreEqual(0, axis.Count);
                                    if (i == 0)
                                    {
                                        Assert.AreSame(axis, resultC);
                                    }
                                    else
                                    {
                                        Assert.AreNotSame(axis, resultC);
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
            object[] pathD = new object[] { new ItemPathNode("tools", 0, new object[] { new Tool() }), "retraction", "length" };
            object? resultD = DuetControlServer.IPC.Processors.ModelSubscription.GetPathNode(root, pathD);

            Assert.AreEqual(4, root.Count);
            if (root.TryGetValue("tools", out object? toolsObject))
            {
                if (toolsObject is List<object> tools)
                {
                    Assert.AreEqual(1, tools.Count);
                    if (tools[0] is Dictionary<string, object?> tool)
                    {
                        if (tool.TryGetValue("retraction", out object? retractionObject))
                        {
                            if (retractionObject is Dictionary<string, object?> retraction)
                            {
                                Assert.AreSame(retraction, resultD);
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
