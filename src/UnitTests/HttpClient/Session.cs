using DuetAPI.ObjectModel;
using DuetHttpClient;
using DuetHttpClient.Utility;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.HttpClient
{
    [TestFixture]
    public class Session
    {
        private DuetHttpSession? session;

        [OneTimeSetUp]
        public async Task Connect()
        {
            // It may be necessary to assign an error handler first so that JSON errors are caught and the ObjectModel test completes
            DuetAPI.ObjectModel.ObjectModel.OnDeserializationFailed += delegate { };

            // This has to be changed depending on the test setup.
            // The IP address must be either a Duet (standalone mode) or a SBC running DSF (SBC mode)
            session = await DuetHttpSession.ConnectAsync(new("http://ender5pro.fritz.box"));
        }

        [Test]
        public async Task ObjectModel()
        {
            // Wait for the object model to be up-to-date
            await session!.WaitForModelUpdate();
            Assert.AreNotEqual(MachineStatus.Starting, session.Model.State.Status);

            // Save the current uptime
            int now;
            lock (session.Model)
            {
                now = session.Model.State.UpTime;
            }

            // Wait again for UpTime to change. Because other things may update the OM in SBC mode, we await an extra delay first
            await Task.Delay(1000);
            await session.WaitForModelUpdate();

            // Make sure the object model is updated
            lock (session.Model)
            {
                Assert.Greater(session.Model.State.UpTime, now);
            }
        }

        [Test]
        public async Task Codes()
        {
            // Make sure there are no timeouts
            await session!.SendCode("G4 S6");

            // Check generic G-code reply
            string response = await session.SendCode("M115");
            Assert.IsTrue(response.StartsWith("FIRMWARE"));
        }

        [Test]
        public async Task Files()
        {
            string uploadContent = Guid.NewGuid().ToString();

            // Upload a test file
            await using (MemoryStream uploadStream = new())
            {
                uploadStream.Write(Encoding.UTF8.GetBytes(uploadContent));
                uploadStream.Seek(0, SeekOrigin.Begin);

                await session!.Upload("0:/sys/unitTest.txt", uploadStream);
            }

            // Download it again
            using (HttpResponseMessage downloadResponse = await session.Download("0:/sys/unitTest.txt"))
            {
                string downloadContent = await downloadResponse.Content.ReadAsStringAsync();
                Assert.AreEqual(uploadContent, downloadContent);
            }

            // Move it
            await session!.Move("0:/sys/unitTest.txt", "0:/sys/unitTest2.txt", true);

            // Delete it again
            await session!.Delete("0:/sys/unitTest2.txt");
        }

        [Test]
        public async Task Directories()
        {
            // Create a new directory
            await session!.MakeDirectory("0:/sys/unitTest");

            // Delete it again
            await session!.Delete("0:/sys/unitTest");
        }

        [Test]
        public async Task FileList()
        {
            // List files in 0:/sys and check for valid config.g
            IEnumerable<FileListItem> fileList = await session!.GetFileList("0:/sys");
            Assert.IsTrue(fileList.Any(item => !item.IsDirectory && item.Filename == "config.g" && item.Size > 0 && item.Size < 192_000));

            // List root directories and check for sys directory
            fileList = await session!.GetFileList("0:/");
            Assert.IsTrue(fileList.Any(item => item.IsDirectory && item.Filename == "sys"));
        }

        [Test]
        public async Task FileInfo()
        {
            // Get fileinfo for 0:/sys/config.g
            GCodeFileInfo info = await session!.GetFileInfo("0:/sys/config.g");
            Assert.Greater(info.Size, 0);
            Assert.Less(info.Size, 192_000);
        }

        [OneTimeTearDown]
        public async Task Disconnect()
        {
            await session!.DisposeAsync();
        }
    }
}
