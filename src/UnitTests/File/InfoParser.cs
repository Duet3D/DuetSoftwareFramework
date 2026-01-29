namespace UnitTests.File
{
    // DISABLED: InfoParser class no longer exists - needs refactoring
    /*
    public class InfoParser
    {
        [Test]
        [TestCase("Cura.gcode")]
        [TestCase("PrusaSlicer.gcode")]
        [TestCase("Simplify3D.gcode")]
        [TestCase("Slic3r.gcode")]
        public async Task Test(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.ParseAsync(filePath, true);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            Assert.That(info.FileName, Is.Not.Null);
            Assert.That(info.Size, Is.Not.EqualTo(0));
            Assert.That(info.Height, Is.Not.EqualTo(0));
            Assert.That(info.LayerHeight, Is.Not.EqualTo(0));
            Assert.That(info.NumLayers, Is.Not.EqualTo(0));
            Assert.That(info.Filament.Count, Is.Not.EqualTo(0));
            Assert.That(info.GeneratedBy, Is.Not.Empty);
            // Assert.That(info.PrintTime, Is.Not.EqualTo(0));
            // Assert.That(info.SimulatedTime, Is.Not.EqualTo(0));
        }

        [TestCase("Thumbnail.gcode", 2)]
        [TestCase("Thumbnail_JPG.gcode", 1)]
        [TestCase("Thumbnail_QOI.gcode", 2)]
        [TestCase("BenchyIcon.gcode", 1)]
        public async Task TestThumbnails(string fileName, int thumbnailCount)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.ParseAsync(filePath, true);
            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));
            Assert.That(info.Thumbnails.Count, Is.EqualTo(thumbnailCount));
        }

        [TestCase("Thumbnail.gcode")]
        public async Task TestThumbnailResponse(string fileName)
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.ParseAsync(filePath, true);

            string thumbnailResponse = await DuetControlServer.Files.InfoParser.ParseFileFragment(filePath, info.Thumbnails[0].Offset, true);
            Assert.That(thumbnailResponse.Contains(info.Thumbnails[0].Data![..1024]), Is.True);

            TestContext.Out.Write(thumbnailResponse);
        }

        [Test]
        public async Task TestEmpty()
        {
            string filePath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes/Circle.gcode");
            GCodeFileInfo info = await DuetControlServer.Files.InfoParser.ParseAsync(filePath, true);

            TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

            Assert.That(info.FileName, Is.Not.Null);
            Assert.That(info.Size, Is.Not.EqualTo(0));
            Assert.That(info.Height, Is.EqualTo(0.5));
            Assert.That(info.LayerHeight, Is.EqualTo(0));
            Assert.That(info.Filament.Count, Is.EqualTo(0));
            Assert.That(info.GeneratedBy, Is.Null);
            Assert.That(info.PrintTime, Is.Null);
            Assert.That(info.SimulatedTime, Is.Null);
        }
    }
    */
}
