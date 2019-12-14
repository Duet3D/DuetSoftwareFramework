using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class HttpEndpoints
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.HttpEndpoints.Add(new HttpEndpoint { EndpointType = HttpEndpointType.PATCH, Namespace = "my-demo", Path = "ep", UnixSocket = "/tmp/some.sock" });

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.HttpEndpoints.Count, clone.HttpEndpoints.Count);
            Assert.AreEqual(original.HttpEndpoints[0].EndpointType, clone.HttpEndpoints[0].EndpointType);
            Assert.AreEqual(original.HttpEndpoints[0].Namespace, clone.HttpEndpoints[0].Namespace);
            Assert.AreEqual(original.HttpEndpoints[0].Path, clone.HttpEndpoints[0].Path);
            Assert.AreEqual(original.HttpEndpoints[0].UnixSocket, clone.HttpEndpoints[0].UnixSocket);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.HttpEndpoints.Add(new HttpEndpoint { EndpointType = HttpEndpointType.PATCH, Namespace = "my-demo", Path = "ep", UnixSocket = "/tmp/some.sock" });

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.UserVariables.Count, assigned.UserVariables.Count);
            Assert.AreEqual(original.HttpEndpoints[0].EndpointType, assigned.HttpEndpoints[0].EndpointType);
            Assert.AreEqual(original.HttpEndpoints[0].Namespace, assigned.HttpEndpoints[0].Namespace);
            Assert.AreEqual(original.HttpEndpoints[0].Path, assigned.HttpEndpoints[0].Path);
            Assert.AreEqual(original.HttpEndpoints[0].UnixSocket, assigned.HttpEndpoints[0].UnixSocket);
        }
    }
}
