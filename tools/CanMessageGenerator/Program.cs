using System.Text.Json.Nodes;
using CanMessageGenerator.Emit;
using CanMessageGenerator.Model;

namespace CanMessageGenerator;

/// <summary>
/// Generates the C++ and C# representations of the Duet 3 CAN message formats from the neutral schema,
/// together with the conformance harnesses that verify their layouts.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Dictionary<string, string> options = ParseArgs(args, out bool check);
        string repoRoot = options.GetValueOrDefault("root") ?? FindRepoRoot();
        string schemaPath = options.GetValueOrDefault("schema")
            ?? Path.Combine(repoRoot, "tools/CanMessageGenerator/Schema/can-messages.json");

        CanSchema schema;
        try
        {
            schema = CanSchema.Load(schemaPath);
            ExpandTemplates(schema);
            LayoutEngine.ComputeAll(schema);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }

        CppEmitter cpp = new(schema);
        CppTablesEmitter cppTables = new(schema);
        CSharpEmitter csharp = new(schema);
        CSharpTablesEmitter csharpTables = new(schema);
        ConformanceEmitter conformance = new(schema);
        List<LayoutFacts> facts = conformance.Collect();

        string csharpDir = options.GetValueOrDefault("csharp-out")
            ?? Path.Combine(repoRoot, "src/DuetControlServer/Link/Protocol/CanMessages/Generated");
        string testDir = options.GetValueOrDefault("test-out")
            ?? Path.Combine(repoRoot, "src/UnitTests/Link");

        List<(string Path, string Content)> outputs =
        [
            (options.GetValueOrDefault("cpp-out") ?? Path.Combine(repoRoot, "tools/CanMessageGenerator/generated/cpp/CanMessageFormats.h"),
             cpp.Emit()),
            (options.GetValueOrDefault("cpp-tables-out") ?? Path.Combine(repoRoot, "tools/CanMessageGenerator/generated/cpp/CanMessageGenericTables.h"),
             cppTables.Emit()),
            (Path.Combine(csharpDir, "CanMessageFormats.g.cs"), csharp.EmitStructs()),
            (Path.Combine(csharpDir, "CanMessageUnion.g.cs"), csharp.EmitUnion()),
            (Path.Combine(csharpDir, "CanMessageBuffers.g.cs"), csharp.EmitBuffers()),
            (Path.Combine(csharpDir, "CanMessageSupport.g.cs"), csharp.EmitSupport()),
            (Path.Combine(csharpDir, "CanGenericTables.g.cs"), csharpTables.Emit()),
            (Path.Combine(csharpDir, "CanGenericBuilders.g.cs"), csharpTables.EmitBuilders()),
            (options.GetValueOrDefault("probe-out") ?? Path.Combine(repoRoot, "tools/CanMessageGenerator/generated/cpp/CanMessageLayoutProbe.cpp"),
             conformance.EmitCppProbe(facts)),
            (options.GetValueOrDefault("tables-probe-out") ?? Path.Combine(repoRoot, "tools/CanMessageGenerator/generated/cpp/CanMessageGenericTablesProbe.cpp"),
             conformance.EmitCppTablesProbe()),
            (Path.Combine(testDir, "CanMessageLayout.g.cs"), conformance.EmitCSharpTests(facts, "UnitTests.Link")),
            (Path.Combine(testDir, "CanGenericTableLayout.g.cs"), conformance.EmitCSharpTablesTests("UnitTests.Link"))
        ];

        int stale = 0;
        foreach ((string path, string content) in outputs)
        {
            if (check)
            {
                string existing = File.Exists(path) ? File.ReadAllText(path) : "";
                if (existing != content)
                {
                    Console.Error.WriteLine($"out of date: {Path.GetRelativePath(repoRoot, path)}");
                    stale++;
                }
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content);
                Console.WriteLine($"wrote {Path.GetRelativePath(repoRoot, path)}");
            }
        }

        if (check)
        {
            if (stale > 0)
            {
                Console.Error.WriteLine($"{stale} generated file(s) do not match the schema; run 'make can-messages'");
                return 1;
            }
            Console.WriteLine($"all generated CAN message files are up to date ({facts.Count} structs)");
            return 0;
        }

        Console.WriteLine($"generated {facts.Count} CAN message structs, {facts.Sum(f => f.BitPatterns.Count)} bitfields");
        return 0;
    }

    /// <summary>
    /// Replace each template struct with the concrete instantiations that the C# side needs. C++ keeps
    /// the template itself, so the template's own definition stays in the C++ output only.
    /// </summary>
    private static void ExpandTemplates(CanSchema schema)
    {
        List<StructDef> expanded = [];
        foreach (StructDef template in schema.Structs.Where(s => s.TemplateParam is not null).ToList())
        {
            foreach (InstantiationDef instantiation in template.Instantiations)
            {
                StructDef concrete = new()
                {
                    Name = template.Name + instantiation.Suffix,
                    Doc = template.Doc,
                    Packed = template.Packed,
                    IsUnion = template.IsUnion,
                    MessageType = template.MessageType,
                    Emit = [Language.CSharp],
                    Constants = template.Constants,
                    Members = [.. template.Members.Select(CloneMember)],
                    Methods = template.Methods,
                    RequestIdField = template.RequestIdField,
                    SetRequestIdAlsoClears = template.SetRequestIdAlsoClears,
                    ClearAlsoClears = template.ClearAlsoClears,
                    TemplateArg = instantiation.Arg,
                    TemplateParamName = template.TemplateParam,
                    TemplateOf = template.Name
                };
                foreach (MemberDef m in concrete.Members.Where(m => m.Type == template.TemplateParam))
                {
                    m.Type = instantiation.Arg;
                }
                expanded.Add(concrete);
            }
            template.Emit = [Language.Cpp];
        }
        schema.Structs.AddRange(expanded);
    }

    /// <summary>
    /// Deep-copy a member so that expanding a template does not disturb the template itself. Every
    /// declared field is copied; the layout fields (Offset, Size, ResolvedLength) are deliberately left
    /// at their defaults because <see cref="LayoutEngine"/> fills those in for the expansion.
    /// </summary>
    private static MemberDef CloneMember(MemberDef m) => new()
    {
        Kind = m.Kind,
        Name = m.Name,
        Type = m.Type,
        Doc = m.Doc,
        Length = m.Length,
        Storage = m.Storage,
        Fields = [.. m.Fields.Select(f => new BitFieldDef
        {
            Name = f.Name, Width = f.Width, Bool = f.Bool, Signed = f.Signed, Reserved = f.Reserved,
            Doc = f.Doc, CppAccessPath = f.CppAccessPath
        })],
        Anonymous = m.Anonymous,
        Alternatives = [.. m.Alternatives.Select(CloneMember)],
        Unaligned = m.Unaligned,
        Reserved = m.Reserved,
        CppPrivate = m.CppPrivate,
        CppAccessPath = m.CppAccessPath
    };

    private static Dictionary<string, string> ParseArgs(string[] args, out bool check)
    {
        Dictionary<string, string> options = [];
        check = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--check")
            {
                check = true;
            }
            else if (arg.StartsWith("--") && i + 1 < args.Length)
            {
                options[arg[2..]] = args[++i];
            }
        }
        return options;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")) && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
