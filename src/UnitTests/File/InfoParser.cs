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
        _parser = new FileInfoParser(new CodeFactory(serviceProvider), expressions, new FilePathResolver(model, settings), model, NullLogger<FileInfoParser>.Instance, settings);
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
    [TestCase("preFlight", "; generated by preFlight 1.4.0 on 2026-09-25\n; print_height = 20.000\n; layer_count = 100\n; layer_height = 0.2\n; filament used [mm] = 1234.5\n")]
    [TestCase("Fusion360",";Generated by Fusion 360\n;Height: 20mm\n;Layer height: 0.2\n;Filament used: 1.2345m\n;NUM_LAYERS: 100\n")]
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

    [Test]
    public async Task TestSilentModePrintTimeIsIgnored()
    {
        string filePath = await WriteSyntheticJobAsync("PrusaSlicer_SilentMode.gcode", "; generated by PrusaSlicer 2.9.0 on 2026-09-04\n; layer_height = 0.2\n; filament used [mm] = 1234.5\n", "; estimated printing time (normal mode) = 1h 5m 41s\n; estimated printing time (silent mode) = 1h 9m 3s\n");
        GCodeFileInfo info = await _parser.ParseAsync(filePath, false);

        Assert.That(info.PrintTime, Is.EqualTo(3941));
    }

    /// <summary>
    /// Write a job file consisting of the given header comments and a single Z move
    /// </summary>
    private static async Task<string> WriteHeaderOnlyJobAsync(string header)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"Header_{Guid.NewGuid():N}.gcode");
        await System.IO.File.WriteAllTextAsync(filePath, $"{header}G90\nG1 Z1\n");
        return filePath;
    }

    [TestCase(";GENERATOR.NAME: Pathio\n", "Pathio")]
    [TestCase(";Fusion version: 2.0.1234\n", "Fusion version: 2.0.1234")]
    [TestCase(";Generated with Cura_SteamEngine 5.10.0\n", "Cura_SteamEngine 5.10.0")]
    [TestCase(";Sliced by ideaMaker 4.2.3\n", "ideaMaker 4.2.3")]
    public async Task TestGeneratedBy(string header, string generatedBy)
    {
        GCodeFileInfo info = await _parser.ParseAsync(await WriteHeaderOnlyJobAsync(header), false);
        Assert.That(info.GeneratedBy, Is.EqualTo(generatedBy));
    }

    [TestCase(";Layer count: 125\n", 125)]
    [TestCase(";LAYER_COUNT:100\n", 100)]
    [TestCase("; layer_count = 60\n", 60)]
    public async Task TestNumLayers(string header, int numLayers)
    {
        GCodeFileInfo info = await _parser.ParseAsync(await WriteHeaderOnlyJobAsync(header), false);
        Assert.That(info.NumLayers, Is.EqualTo(numLayers));
    }

    [TestCase(";Print time: 40m:36s\n", 2436)]
    [TestCase(";Print time: 1h:20m:36s\n", 4836)]
    [TestCase(";Print Time: 1234\n", 1234)]
    [TestCase(";PRINT.TIME: 1234\n", 1234)]
    [TestCase(";TIME 3720.97\n", 3721)]
    [TestCase(";TIME:38846\n", 38846)]
    [TestCase("; Estimated Build Time:   332.83 minutes\n", 19980)]
    public async Task TestPrintTime(string header, long printTime)
    {
        GCodeFileInfo info = await _parser.ParseAsync(await WriteHeaderOnlyJobAsync(header), false);
        Assert.That(info.PrintTime, Is.EqualTo(printTime));
    }

    [TestCase(";Extruder 1 material used: 1811mm\n", 1811)]
    [TestCase(";   Material Length: 13572.2 mm (13.57 m)\n", 13572.2)]
    [TestCase(";   Filament length: 13572.2 mm (13.57 m)\n", 13572.2)]
    [TestCase("; Estimated Build Volume: 32.5 cm^3\n", 13511.93)]
    public async Task TestFilamentUsed(string header, double filament)
    {
        GCodeFileInfo info = await _parser.ParseAsync(await WriteHeaderOnlyJobAsync(header), false);

        Assert.That(info.Filament, Has.Count.EqualTo(1));
        Assert.That(info.Filament[0], Is.EqualTo(filament).Within(0.01));
    }

    [Test]
    public async Task TestCustomInfo()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "CustomInfo.gcode");
        await System.IO.File.WriteAllTextAsync(filePath, ";customInfo tight=1\n; customInfo spaced = 2\n; CustomInfo capital = 3\n; -- customInfo dashed = 4\n; customInfo dup = 5\n; customInfo dup = 6\n; customInfo 1invalid = 7\n; customInformation other = 8\nG1 Z1\n");
        GCodeFileInfo info = await _parser.ParseAsync(filePath, false);

        Assert.That(info.CustomInfo.Keys, Is.EquivalentTo(new[] { "tight", "spaced", "capital", "dashed", "dup" }));
        Assert.That(info.CustomInfo["spaced"]!.Value.GetInt32(), Is.EqualTo(2));
        Assert.That(info.CustomInfo["capital"]!.Value.GetInt32(), Is.EqualTo(3));
        Assert.That(info.CustomInfo["dup"]!.Value.GetInt32(), Is.EqualTo(5));
    }
}
