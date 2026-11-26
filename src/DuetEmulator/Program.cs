using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuetEmulation;
using DuetEmulation.Captures;
using DuetEmulation.Scenarios;
using DuetEmulator;

Option<string?> stackOption = new("--stack")
{
    Description = "Run on this stack only (rrf or dsf) rather than on those the scenario names"
};
Option<DirectoryInfo?> outOption = new("--out", "-o")
{
    Description = "Where the captures and logs go (default: build/emulation/scenarios/<scenario>)"
};
Option<bool> failOnDifferenceOption = new("--fail-on-difference")
{
    Description = "Exit with 1 if the stacks' captures differ"
};

Option<double?> timeToleranceOption = new("--time-tolerance")
{
    Description = "Compare when events happen within their step, to this many seconds; timing is ignored without it"
};
Option<string[]> ignorePathOption = new("--ignore-path")
{
    Description = "Leave out an object model path and what is under it ([*] matches any index); replaces the defaults",
    AllowMultipleArgumentsPerToken = true
};
Option<string[]> ignoreCanTypeOption = new("--ignore-can-type")
{
    Description = "Leave out a CAN message type; replaces the defaults, which are the periodic ones (TimeSync and the status and fan reports)",
    AllowMultipleArgumentsPerToken = true
};
Option<bool> noPinsOption = new("--no-pins")
{
    Description = "Do not compare pin changes"
};

ComparisonOptions ComparisonFrom(ParseResult result)
{
    ComparisonOptions options = new() { TimeTolerance = result.GetValue(timeToleranceOption), ComparePins = !result.GetValue(noPinsOption) };
    if (result.GetValue(ignorePathOption) is { Length: > 0 } paths)
    {
        options = options with { IgnoredPaths = paths };
    }
    if (result.GetValue(ignoreCanTypeOption) is { Length: > 0 } types)
    {
        options = options with { IgnoredCanTypes = types };
    }
    return options;
}

// run
Argument<FileInfo> scenarioArgument = new("scenario") { Description = "The scenario file (YAML)" };
Command runCommand = new("run", "Run a scenario on each of its stacks, save the captures, and compare them")
{
    scenarioArgument, stackOption, outOption, failOnDifferenceOption, timeToleranceOption, ignorePathOption, ignoreCanTypeOption, noPinsOption
};
runCommand.SetAction(async (result, token) =>
{
    Scenario scenario = Scenario.Load(result.GetRequiredValue(scenarioArgument).FullName);
    IReadOnlyList<Stack> stacks = result.GetValue(stackOption) is string only ? [Scenario.ParseStack(only)] : scenario.StackList();
    string output = result.GetValue(outOption)?.FullName ?? Path.Combine(RepositoryPaths.Emulation, "scenarios", Output.SafeName(scenario.Name));

    List<MachineCapture> captures = [];
    foreach (Stack stack in stacks)
    {
        string directory = Path.Combine(output, stack.ToString().ToLowerInvariant());
        Console.Error.WriteLine($"Running {scenario.Name} on {stack}...");
        MachineCapture capture = await ScenarioRunner.RunAsync(scenario, stack, directory, token);
        captures.Add(capture);
        Console.WriteLine(capture.Report());
        Console.WriteLine($"Capture: {Path.Combine(directory, MachineCapture.FileName)}");
        Console.WriteLine();
    }

    if (captures.Count == 2)
    {
        IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(captures[0], captures[1], ComparisonFrom(result));
        string report = CaptureComparison.Report(differences, captures[0], captures[1]);
        await File.WriteAllTextAsync(Path.Combine(output, "differences.txt"), report, token);
        Console.WriteLine(report);
        return differences.Count > 0 && result.GetValue(failOnDifferenceOption) ? 1 : 0;
    }
    return 0;
});

// diff
Argument<string> firstArgument = new("first") { Description = "A capture, or the directory it is in" };
Argument<string> secondArgument = new("second") { Description = "Another capture of the same script" };
Command diffCommand = new("diff", "Compare two captures step by step")
{
    firstArgument, secondArgument, timeToleranceOption, ignorePathOption, ignoreCanTypeOption, noPinsOption
};
diffCommand.SetAction(result =>
{
    MachineCapture first = MachineCapture.Load(result.GetRequiredValue(firstArgument));
    MachineCapture second = MachineCapture.Load(result.GetRequiredValue(secondArgument));
    IReadOnlyList<CaptureDifference> differences = CaptureComparison.Compare(first, second, ComparisonFrom(result));
    Console.WriteLine(CaptureComparison.Report(differences, first, second));
    return differences.Count == 0 ? 0 : 1;
});

// show
Argument<string> captureArgument = new("capture") { Description = "A capture, or the directory it is in" };
Command showCommand = new("show", "Print a capture as readable text") { captureArgument };
showCommand.SetAction(result => Console.WriteLine(MachineCapture.Load(result.GetRequiredValue(captureArgument)).Report()));

// serve
Option<int> portOption = new("--port", "-p")
{
    Description = "The port to listen on, on localhost",
    DefaultValueFactory = _ => SessionServer.DefaultPort
};
Command serveCommand = new("serve", "Serve interactive sessions over HTTP on localhost: boot a bench once and drive it step by step")
{
    portOption, outOption
};
serveCommand.SetAction(async (result, token) =>
{
    string output = result.GetValue(outOption)?.FullName ?? Path.Combine(RepositoryPaths.Emulation, "sessions");
    await using SessionServer server = new(result.GetValue(portOption), output);
    Console.Error.WriteLine($"Listening on {server.Prefix}");
    await server.RunAsync(token);
    return 0;
});

RootCommand root = new("Run the Duet emulation bench: scenarios, captures and interactive sessions on RepRapFirmware and DSF")
{
    runCommand, diffCommand, showCommand, serveCommand
};
return await root.Parse(args).InvokeAsync();
