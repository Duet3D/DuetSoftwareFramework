using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UnitTests.Utility;

[TestFixture]
public class FileLists
{
    private string _testDirectory;

    [OneTimeSetUp]
    public void CreateTestDirectory()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"dsf-filelists-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        for (int i = 0; i < 5; i++)
        {
            System.IO.File.WriteAllText(Path.Combine(_testDirectory, $"file{i}.g"), "G91");
        }
    }

    [OneTimeTearDown]
    public void DeleteTestDirectory()
    {
        Directory.Delete(_testDirectory, true);
    }

    [Test]
    public void GetFileListPagination()
    {
        // Request the file list in chunks of two items and make sure the next indices advance
        HashSet<string> seenFiles = [];
        int startAt = 0, numRequests = 0;
        do
        {
            string fileList = DuetAPI.Utility.FileLists.GetFileList("0:/gcodes", _testDirectory, startAt, maxItems: 2);
            using JsonDocument json = JsonDocument.Parse(fileList);

            Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.EqualTo(0));
            Assert.That(json.RootElement.GetProperty("first").GetInt32(), Is.EqualTo(startAt));

            foreach (JsonElement item in json.RootElement.GetProperty("files").EnumerateArray())
            {
                Assert.That(seenFiles.Add(item.GetProperty("name").GetString()), Is.True, "every file must be returned exactly once");
            }

            int next = json.RootElement.GetProperty("next").GetInt32();
            if (next == 0)
            {
                break;
            }

            // The next start index must advance, else clients would loop forever on the same chunk
            Assert.That(next, Is.GreaterThan(startAt));
            startAt = next;
        }
        while (++numRequests < 10);

        Assert.That(seenFiles, Has.Count.EqualTo(5));
    }

    [Test]
    public void GetFilesPagination()
    {
        // Same as above for the rr_files-style file list
        HashSet<string> seenFiles = [];
        int startAt = 0, numRequests = 0;
        do
        {
            string fileList = DuetAPI.Utility.FileLists.GetFiles("0:/gcodes", _testDirectory, startAt, maxItems: 2);
            using JsonDocument json = JsonDocument.Parse(fileList);

            Assert.That(json.RootElement.GetProperty("err").GetInt32(), Is.EqualTo(0));

            foreach (JsonElement item in json.RootElement.GetProperty("files").EnumerateArray())
            {
                Assert.That(seenFiles.Add(item.GetString()), Is.True, "every file must be returned exactly once");
            }

            int next = json.RootElement.GetProperty("next").GetInt32();
            if (next == 0)
            {
                break;
            }

            Assert.That(next, Is.GreaterThan(startAt));
            startAt = next;
        }
        while (++numRequests < 10);

        Assert.That(seenFiles, Has.Count.EqualTo(5));
    }
}
