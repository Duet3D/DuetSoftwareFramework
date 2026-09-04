using DuetAPI.ObjectModel;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Codes.Handlers;
using DuetControlServer.Codes.Meta;
using DuetControlServer.Files;
using DuetControlServer.Files.Parser;
using DuetControlServer.Link;
using DuetControlServer.Link.Channel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DcsModel = DuetControlServer.Model.ObjectModel;
using DcsFilter = DuetControlServer.Model.Filter;

namespace UnitTests.File;

[TestFixture]
public class InfoParser
{
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class NullCodeHandler : ICodeHandler
    {
        public ValueTask<Message?> ProcessAsync(DuetControlServer.Commands.Code code, CancellationToken cancellationToken) => ValueTask.FromResult<Message?>(null);
        public ValueTask CodeExecutedAsync(DuetControlServer.Commands.Code code, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private FileInfoParser _parser = null!;

    [SetUp]
    public void SetUp()
    {
        IOptions<Settings> settings = Options.Create(new Settings());
        TestLifetime lifetime = new();
        DcsModel model = new(lifetime, NullLogger<DcsModel>.Instance, settings);
        Expressions expressions = new(new DcsFilter(model), model, null!);

        // The code factory activates DCS code instances, which pull in the whole code processing graph
        ServiceProvider serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(settings)
            .AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddSingleton(model)
            .AddSingleton(expressions)
            .AddSingleton(provider => new LinkInterface(new Manager(provider), null!, NullLogger<LinkInterface>.Instance, settings))
            .AddSingleton(provider => new CodeProcessor(expressions, model, lifetime, provider))
            .AddKeyedSingleton<ICodeHandler>(Keys.GCodes, new NullCodeHandler())
            .AddKeyedSingleton<ICodeHandler>(Keys.MCodes, new NullCodeHandler())
            .AddKeyedSingleton<ICodeHandler>(Keys.TCodes, new NullCodeHandler())
            .AddKeyedSingleton<ICodeHandler>(Keys.Keywords, new NullCodeHandler())
            .BuildServiceProvider();
        _parser = new FileInfoParser(new CodeFactory(serviceProvider), expressions, new FilePathResolver(model, settings), NullLogger<FileInfoParser>.Instance, settings);
    }

    private static string GetTestFile(string fileName) => Path.Combine(Directory.GetCurrentDirectory(), "../../../File/GCodes", fileName);

    [Test]
    [TestCase("Cura.gcode")]
    [TestCase("PrusaSlicer.gcode")]
    [TestCase("Simplify3D.gcode")]
    [TestCase("Slic3r.gcode")]
    public async Task Test(string fileName)
    {
        GCodeFileInfo info = await _parser.ParseAsync(GetTestFile(fileName), true);

        TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

        Assert.That(info.FileName, Is.Not.Null);
        Assert.That(info.Size, Is.Not.EqualTo(0));
        Assert.That(info.Height, Is.Not.EqualTo(0));
        Assert.That(info.LayerHeight, Is.Not.EqualTo(0));
        Assert.That(info.NumLayers, Is.Not.EqualTo(0));
        Assert.That(info.Filament, Is.Not.Empty);
        Assert.That(info.GeneratedBy, Is.Not.Empty);
    }

    [TestCase("Thumbnail.gcode", 2)]
    [TestCase("Thumbnail_JPG.gcode", 1)]
    [TestCase("Thumbnail_QOI.gcode", 2)]
    [TestCase("BenchyIcon.gcode", 1)]
    public async Task TestThumbnails(string fileName, int thumbnailCount)
    {
        GCodeFileInfo info = await _parser.ParseAsync(GetTestFile(fileName), true);
        TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));
        Assert.That(info.Thumbnails, Has.Count.EqualTo(thumbnailCount));
    }

    [TestCase("Thumbnail.gcode")]
    public async Task TestThumbnailResponse(string fileName)
    {
        GCodeFileInfo info = await _parser.ParseAsync(GetTestFile(fileName), true);

        string thumbnailResponse = await _parser.ParseFileFragment(GetTestFile(fileName), info.Thumbnails[0].Offset, true);
        Assert.That(thumbnailResponse, Does.Contain(info.Thumbnails[0].Data![..1024]));

        TestContext.Out.Write(thumbnailResponse);
    }

    [Test]
    public async Task TestEmpty()
    {
        GCodeFileInfo info = await _parser.ParseAsync(GetTestFile("Circle.gcode"), true);

        TestContext.Out.Write(JsonSerializer.Serialize(info, typeof(GCodeFileInfo), new JsonSerializerOptions { WriteIndented = true }));

        Assert.That(info.FileName, Is.Not.Null);
        Assert.That(info.Size, Is.Not.EqualTo(0));
        Assert.That(info.Height, Is.EqualTo(0.5));
        Assert.That(info.LayerHeight, Is.EqualTo(0));
        Assert.That(info.Filament, Is.Empty);
        Assert.That(info.GeneratedBy, Is.Null);
        Assert.That(info.PrintTime, Is.Null);
        Assert.That(info.SimulatedTime, Is.Null);
    }

    /// <summary>
    /// Write a synthetic job file with 100 layers of 0.2mm whose last layer is bigger than the footer read limit and ends without a Z move
    /// </summary>
    private static async Task<string> WriteSyntheticJobAsync(string name, string header, string footer)
    {
        StringBuilder builder = new(header);
        builder.Append("G90\nM83\nG28\n");
        for (int layer = 1; layer <= 100; layer++)
        {
            builder.Append(CultureInfo.InvariantCulture, $";LAYER_CHANGE\n;Z:{layer * 0.2:F2}\n;HEIGHT:0.2\nG1 Z{layer * 0.2:F2} F600\n");
            int numMoves = (layer < 100) ? 20 : 16384;
            for (int move = 0; move < numMoves; move++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"G1 X{move % 300}.123 Y{(move * 7) % 300}.456 E0.01234\n");
            }
        }
        builder.Append("M98 P\"0:/sys/print_end\"\n");
        builder.Append(footer);

        string filePath = Path.Combine(Path.GetTempPath(), name);
        await System.IO.File.WriteAllTextAsync(filePath, builder.ToString());
        return filePath;
    }

    [TestCase("OrcaSlicer", "; generated by OrcaSlicer 2.3.0 on 2026-09-04\n; max_z_height: 20.00\n; total layer number: 100\n; layer_height = 0.2\n; filament used [mm] = 1234.5\n")]
    [TestCase("Cura", ";FLAVOR:RepRap\n;Filament used: 1.2345m\n;Layer height: 0.2\n;MAXZ:20\n;Generated with Cura_SteamEngine 5.10.0\n;LAYER_COUNT:100\n")]
    [TestCase("Fusion360", ";Generated by Fusion 360\n;Height: 20mm\n;Layer height: 0.2\n;Filament used: 1.2345m\n;NUM_LAYERS: 100\n")]
    public async Task TestHeightFromHeaderComment(string slicer, string header)
    {
        string filePath = await WriteSyntheticJobAsync($"{slicer}_BigLastLayer.gcode", header, string.Empty);
        GCodeFileInfo info = await _parser.ParseAsync(filePath, false);

        Assert.That(info.Size, Is.GreaterThan(new Settings().FileInfoReadLimitFooter));
        Assert.That(info.Height, Is.EqualTo(20).Within(0.001));
        Assert.That(info.LayerHeight, Is.EqualTo(0.2).Within(0.001));
        Assert.That(info.NumLayers, Is.EqualTo(100));
    }

    [Test]
    public async Task TestHeightFromFooterCommentOverridesLastZMove()
    {
        string filePath = await WriteSyntheticJobAsync("FooterHeightComment.gcode", "; generated by OrcaSlicer 2.3.0 on 2026-09-04\n; layer_height = 0.2\n; filament used [mm] = 1234.5\n", "; max_z_height: 20.00\nG1 Z25 F600\n");
        GCodeFileInfo info = await _parser.ParseAsync(filePath, false);

        Assert.That(info.Height, Is.EqualTo(20).Within(0.001));
        Assert.That(info.NumLayers, Is.EqualTo(100));
    }

    [Test]
    public async Task TestPerLayerHeightCommentsAreIgnored()
    {
        string filePath = await WriteSyntheticJobAsync("PrusaSlicer_LayerComments.gcode", "; generated by PrusaSlicer 2.9.0 on 2026-09-04\n; layer_height = 0.2\n; filament used [mm] = 1234.5\n", "G1 Z25 F600\n; max_layer_height = 0.25\n; max_print_height = 250\n");
        GCodeFileInfo info = await _parser.ParseAsync(filePath, false);

        Assert.That(info.Height, Is.EqualTo(25).Within(0.001));
    }
}
