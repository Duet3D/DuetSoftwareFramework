using DuetAPI.Machine;
using NUnit.Framework;

namespace DuetUnitTest.Machine
{
    [TestFixture]
    public class Network
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();

            NetworkInterface iface = new NetworkInterface();
            iface.ActiveProtocols.Add(NetworkProtocol.Telnet);
            iface.MacAddress = "DE:AD:BE:EF:CA:FE";
            iface.ActualIP = "12.34.56.78";
            iface.ConfiguredIP = "34.34.56.78";
            iface.FirmwareVersion = "Firmware version";
            iface.Gateway = "12.34.56.1";
            iface.NumReconnects = 123;
            iface.Signal = -45;
            iface.Speed = 56;
            iface.Subnet = "255.0.255.0";
            iface.Type = InterfaceType.LAN;
            original.Network.Interfaces.Add(iface);

            original.Network.Name = "Name";
            original.Network.Password = "Password";

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(1, original.Network.Interfaces.Count);
            Assert.AreEqual(original.Network.Interfaces[0].ActiveProtocols, clone.Network.Interfaces[0].ActiveProtocols);
            Assert.AreEqual(original.Network.Interfaces[0].MacAddress, clone.Network.Interfaces[0].MacAddress);
            Assert.AreEqual(original.Network.Interfaces[0].ActualIP, clone.Network.Interfaces[0].ActualIP);
            Assert.AreEqual(original.Network.Interfaces[0].ConfiguredIP, clone.Network.Interfaces[0].ConfiguredIP);
            Assert.AreEqual(original.Network.Interfaces[0].FirmwareVersion, clone.Network.Interfaces[0].FirmwareVersion);
            Assert.AreEqual(original.Network.Interfaces[0].Gateway, clone.Network.Interfaces[0].Gateway);
            Assert.AreEqual(original.Network.Interfaces[0].NumReconnects, clone.Network.Interfaces[0].NumReconnects);
            Assert.AreEqual(original.Network.Interfaces[0].Signal, clone.Network.Interfaces[0].Signal);
            Assert.AreEqual(original.Network.Interfaces[0].Speed, clone.Network.Interfaces[0].Speed);
            Assert.AreEqual(original.Network.Interfaces[0].Subnet, clone.Network.Interfaces[0].Subnet);
            Assert.AreEqual(original.Network.Interfaces[0].Type, clone.Network.Interfaces[0].Type);

            Assert.AreEqual(original.Network.Name, clone.Network.Name);
            Assert.AreEqual(original.Network.Password, clone.Network.Password);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();

            NetworkInterface iface = new NetworkInterface();
            iface.ActiveProtocols.Add(NetworkProtocol.Telnet);
            iface.MacAddress = "DE:AD:BE:EF:CA:FE";
            iface.ActualIP = "12.34.56.78";
            iface.ConfiguredIP = "34.34.56.78";
            iface.FirmwareVersion = "Firmware version";
            iface.Gateway = "12.34.56.1";
            iface.NumReconnects = 123;
            iface.Signal = -45;
            iface.Speed = 56;
            iface.Subnet = "255.0.255.0";
            iface.Type = InterfaceType.LAN;
            original.Network.Interfaces.Add(iface);

            original.Network.Name = "Name";
            original.Network.Password = "Password";

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(1, original.Network.Interfaces.Count);
            Assert.AreEqual(original.Network.Interfaces[0].ActiveProtocols, assigned.Network.Interfaces[0].ActiveProtocols);
            Assert.AreEqual(original.Network.Interfaces[0].MacAddress, assigned.Network.Interfaces[0].MacAddress);
            Assert.AreEqual(original.Network.Interfaces[0].ActualIP, assigned.Network.Interfaces[0].ActualIP);
            Assert.AreEqual(original.Network.Interfaces[0].ConfiguredIP, assigned.Network.Interfaces[0].ConfiguredIP);
            Assert.AreEqual(original.Network.Interfaces[0].FirmwareVersion, assigned.Network.Interfaces[0].FirmwareVersion);
            Assert.AreEqual(original.Network.Interfaces[0].Gateway, assigned.Network.Interfaces[0].Gateway);
            Assert.AreEqual(original.Network.Interfaces[0].NumReconnects, assigned.Network.Interfaces[0].NumReconnects);
            Assert.AreEqual(original.Network.Interfaces[0].Signal, assigned.Network.Interfaces[0].Signal);
            Assert.AreEqual(original.Network.Interfaces[0].Speed, assigned.Network.Interfaces[0].Speed);
            Assert.AreEqual(original.Network.Interfaces[0].Subnet, assigned.Network.Interfaces[0].Subnet);
            Assert.AreEqual(original.Network.Interfaces[0].Type, assigned.Network.Interfaces[0].Type);

            Assert.AreEqual(original.Network.Name, assigned.Network.Name);
            Assert.AreEqual(original.Network.Password, assigned.Network.Password);
        }
    }
}
