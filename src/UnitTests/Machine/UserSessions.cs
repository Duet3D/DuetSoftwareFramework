using DuetAPI.Machine;
using NUnit.Framework;

namespace UnitTests.Machine
{
    [TestFixture]
    public class UserSessions
    {
        [Test]
        public void Clone()
        {
            MachineModel original = new MachineModel();
            original.UserSessions.Add(new UserSession { AccessLevel = AccessLevel.ReadOnly, Id = 123, Origin = "192.168.1.123", OriginId = 34545, SessionType = SessionType.HTTP });
            original.UserSessions.Add(new UserSession { AccessLevel = AccessLevel.ReadWrite, Id = 124, Origin = "console", OriginId = -1, SessionType = SessionType.Local });

            MachineModel clone = (MachineModel)original.Clone();

            Assert.AreEqual(original.UserSessions.Count, clone.UserSessions.Count);
            Assert.AreEqual(original.UserSessions[0].AccessLevel, clone.UserSessions[0].AccessLevel);
            Assert.AreEqual(original.UserSessions[0].Id, clone.UserSessions[0].Id);
            Assert.AreEqual(original.UserSessions[0].Origin, clone.UserSessions[0].Origin);
            Assert.AreEqual(original.UserSessions[0].OriginId, clone.UserSessions[0].OriginId);
            Assert.AreEqual(original.UserSessions[0].SessionType, clone.UserSessions[0].SessionType);

            Assert.AreEqual(original.UserSessions[1].AccessLevel, clone.UserSessions[1].AccessLevel);
            Assert.AreEqual(original.UserSessions[1].Id, clone.UserSessions[1].Id);
            Assert.AreEqual(original.UserSessions[1].Origin, clone.UserSessions[1].Origin);
            Assert.AreEqual(original.UserSessions[1].OriginId, clone.UserSessions[1].OriginId);
            Assert.AreEqual(original.UserSessions[1].SessionType, clone.UserSessions[1].SessionType);
        }

        [Test]
        public void Assign()
        {
            MachineModel original = new MachineModel();
            original.UserSessions.Add(new UserSession { AccessLevel = AccessLevel.ReadOnly, Id = 123, Origin = "192.168.1.123", OriginId = 34545, SessionType = SessionType.HTTP });
            original.UserSessions.Add(new UserSession { AccessLevel = AccessLevel.ReadWrite, Id = 124, Origin = "console", OriginId = -1, SessionType = SessionType.Local });

            MachineModel assigned = new MachineModel();
            assigned.Assign(original);

            Assert.AreEqual(original.UserSessions.Count, assigned.UserSessions.Count);
            Assert.AreEqual(original.UserSessions[0].AccessLevel, assigned.UserSessions[0].AccessLevel);
            Assert.AreEqual(original.UserSessions[0].Id, assigned.UserSessions[0].Id);
            Assert.AreEqual(original.UserSessions[0].Origin, assigned.UserSessions[0].Origin);
            Assert.AreEqual(original.UserSessions[0].OriginId, assigned.UserSessions[0].OriginId);
            Assert.AreEqual(original.UserSessions[0].SessionType, assigned.UserSessions[0].SessionType);

            Assert.AreEqual(original.UserSessions[1].AccessLevel, assigned.UserSessions[1].AccessLevel);
            Assert.AreEqual(original.UserSessions[1].Id, assigned.UserSessions[1].Id);
            Assert.AreEqual(original.UserSessions[1].Origin, assigned.UserSessions[1].Origin);
            Assert.AreEqual(original.UserSessions[1].OriginId, assigned.UserSessions[1].OriginId);
            Assert.AreEqual(original.UserSessions[1].SessionType, assigned.UserSessions[1].SessionType);
        }
    }
}
