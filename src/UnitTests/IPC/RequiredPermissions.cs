using DuetAPI.Commands;
using DuetAPI.Utility;
using NUnit.Framework;

namespace UnitTests.IPC
{
    [TestFixture]
    public class RequiredPermissionsTests
    {
        [Test]
        public void NoPermissionsGrantNothing()
        {
            RequiredPermissionsAttribute attribute = new(SbcPermissions.CommandExecution);
            Assert.That(attribute.Check(SbcPermissions.None), Is.False);
        }

        [Test]
        public void UnrelatedPermissionIsRefused()
        {
            RequiredPermissionsAttribute attribute = new(SbcPermissions.SuperUser);
            Assert.That(attribute.Check(SbcPermissions.CommandExecution | SbcPermissions.ObjectModelReadWrite), Is.False);
        }

        [Test]
        public void AnyOfTheRequiredPermissionsIsEnough()
        {
            RequiredPermissionsAttribute attribute = new(SbcPermissions.ObjectModelRead | SbcPermissions.ObjectModelReadWrite);
            Assert.That(attribute.Check(SbcPermissions.ObjectModelRead), Is.True);
            Assert.That(attribute.Check(SbcPermissions.ObjectModelReadWrite | SbcPermissions.CommandExecution), Is.True);
        }

        [Test]
        public void SystemPackageCommandsNeedServicePlugins()
        {
            foreach (System.Type type in new[] { typeof(InstallSystemPackage), typeof(UninstallSystemPackage) })
            {
                RequiredPermissionsAttribute attribute = (RequiredPermissionsAttribute)System.Attribute.GetCustomAttribute(type, typeof(RequiredPermissionsAttribute))!;
                Assert.That(attribute.Check(SbcPermissions.ServicePlugins), Is.True);
                Assert.That(attribute.Check(SbcPermissions.SuperUser), Is.False);
            }
        }
    }
}
