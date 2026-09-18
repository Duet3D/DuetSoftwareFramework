using System;
using System.Collections.Generic;
using System.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace DuetProfiling;

/// <summary>
/// Weaver that surrounds the methods named by ProfileZoneAttribute with a Tracy zone
/// </summary>
/// <remarks>
/// Each woven method gets a call to TracyProfiler.BeginZone before its body and a finally block
/// that calls TracyProfiler.EndZone, so the timeline shows the call tree of the profiled code with
/// no annotations in that code. Argument values are deliberately never touched: passing them to the
/// profiler would mean boxing them, and a method taking a Span cannot have its arguments boxed at
/// all, which rules out the general purpose aspect weavers for a codebase that uses spans.
///
/// On an async method this brackets the synchronous run up to the first await that yields, not the
/// lifetime of the task it returns, because the weaving happens on the method rather than on the
/// state machine that continues it. That is also what keeps the zones valid: Tracy requires a zone
/// to be closed on the thread that opened it and to nest within the zones already open there.
/// </remarks>
public class ModuleWeaver : BaseModuleWeaver
{
    /// <summary>
    /// Namespace holding the profiling support DuetControlServer compiles in when profiling
    /// </summary>
    private const string ProfilingNamespace = "DuetControlServer.Profiling";

    /// <summary>
    /// Attribute naming the code to profile, applied at assembly level
    /// </summary>
    private const string ScopeAttribute = "ProfileZoneAttribute";

    /// <summary>
    /// Class the woven calls are made against
    /// </summary>
    private const string ProfilerType = "TracyProfiler";

    /// <summary>
    /// Class the weaver adds to hold one source location per woven method
    /// </summary>
    private const string ZoneHolderType = "ProfiledZones";

    /// <summary>
    /// Weave the module
    /// </summary>
    public override void Execute()
    {
        List<string> scopes = ReadScopes();
        if (scopes.Count == 0)
        {
            WriteWarning($"No [assembly: ProfileZone] in {ModuleDefinition.Assembly.Name.Name}, so nothing was profiled");
            return;
        }

        TypeDefinition profiler = ModuleDefinition.GetTypes().FirstOrDefault(type => type.Namespace == ProfilingNamespace && type.Name == ProfilerType)
            ?? throw new WeavingException($"{ProfilingNamespace}.{ProfilerType} is not in this assembly, so there is nothing to profile with");
        MethodReference register = Method(profiler, "RegisterZone");
        MethodReference begin = Method(profiler, "BeginZone");
        MethodReference end = Method(profiler, "EndZone");

        TypeDefinition zones = AddZoneHolder();

        int woven = 0;
        Dictionary<string, int> leftAlone = [];
        foreach (TypeDefinition type in ModuleDefinition.GetTypes().ToList())
        {
            if (type == zones || type.Namespace == ProfilingNamespace || !InScope(type, scopes))
            {
                continue;
            }

            foreach (MethodDefinition method in type.Methods.ToList())
            {
                if (!method.HasBody)
                {
                    continue;
                }

                string? reason = ReasonNotToWeave(method);
                if (reason is null)
                {
                    Weave(method, zones.Fields.Count, zones, register, begin, end);
                    woven++;
                }
                else
                {
                    leftAlone.TryGetValue(reason, out int count);
                    leftAlone[reason] = count + 1;
                }
            }
        }

        // Named counts rather than a total, because the question this answers is why a method that
        // was expected on the timeline is not there
        string summary = leftAlone.Count == 0
            ? "nothing"
            : string.Join(", ", leftAlone.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Value} {entry.Key}"));
        WriteInfo($"Profiling: wove {woven} methods in {scopes.Count} scopes; left alone {summary}");
    }

    /// <summary>
    /// Assemblies Fody has to resolve types from for this weaver
    /// </summary>
    /// <returns>Assembly names</returns>
    public override IEnumerable<string> GetAssembliesForScanning() => ["netstandard", "mscorlib", "System.Runtime"];

    /// <summary>
    /// Read the code to profile from the assembly's ProfileZone attributes
    /// </summary>
    /// <returns>Namespace or type names, each covering everything below it</returns>
    private List<string> ReadScopes()
    {
        return ModuleDefinition.Assembly.CustomAttributes
            .Where(attribute => attribute.AttributeType.Name == ScopeAttribute)
            .Select(attribute => attribute.ConstructorArguments[0].Value as string)
            .Where(scope => !string.IsNullOrEmpty(scope))
            .Select(scope => scope!)
            .ToList();
    }

    /// <summary>
    /// Find a method of the profiler to call
    /// </summary>
    /// <param name="profiler">Profiler class</param>
    /// <param name="name">Method name</param>
    /// <returns>Method to call</returns>
    /// <exception cref="WeavingException">Profiler does not have that method</exception>
    private static MethodReference Method(TypeDefinition profiler, string name)
    {
        return profiler.Methods.FirstOrDefault(method => method.Name == name)
            ?? throw new WeavingException($"{profiler.FullName} has no {name} method for the weaver to call");
    }

    /// <summary>
    /// Add the class holding the source location of each woven method
    /// </summary>
    /// <returns>Class to add the fields to</returns>
    /// <remarks>
    /// One static field per woven method, filled in the first time that method runs. Tracy wants a
    /// pointer that stays valid for the life of the process for every zone, and this is what keeps
    /// the per-call cost down to reading a field instead of looking the method up in a dictionary.
    /// </remarks>
    private TypeDefinition AddZoneHolder()
    {
        TypeDefinition holder = new(ProfilingNamespace, ZoneHolderType,
            TypeAttributes.Class | TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit,
            ModuleDefinition.TypeSystem.Object);
        ModuleDefinition.Types.Add(holder);
        return holder;
    }

    /// <summary>
    /// Check whether a type is part of the code to profile
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <param name="scopes">Namespace or type names to profile</param>
    /// <returns>Whether the type is in scope</returns>
    private static bool InScope(TypeDefinition type, List<string> scopes)
    {
        // Nested types have no namespace of their own, so the full name is what is matched against,
        // with the nesting separator made to look like the namespace separator it stands in for
        string name = type.FullName.Replace('/', '.');
        return scopes.Any(scope => name == scope || name.StartsWith(scope + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Work out whether a method has to be left alone
    /// </summary>
    /// <param name="method">Method to check</param>
    /// <returns>Why the method cannot be woven, or null if it can</returns>
    private static string? ReasonNotToWeave(MethodDefinition method)
    {
        if (method.IsConstructor)
        {
            // A constructor's call to its base constructor may not sit inside a protected region
            return "constructors";
        }

        if (method.IsGetter || method.IsSetter)
        {
            // Too numerous and too small for a zone each to say anything
            return "property accessors";
        }

        if (method.ReturnType is ByReferenceType)
        {
            // A by-reference return cannot be held in the local the return value is parked in while
            // the finally block runs
            return "by-reference returns";
        }

        if (IsCompilerGenerated(method) || IsCompilerGenerated(method.DeclaringType))
        {
            // Lambdas, closures, iterators and async state machines. The methods they were written
            // in are woven instead, which is where the names on the timeline come from
            return "compiler generated";
        }

        if (method.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Localloc))
        {
            // localloc is not allowed inside a protected region, so a method using stackalloc
            // cannot have its body wrapped in the try/finally the zone needs
            return "using stackalloc";
        }

        return null;
    }

    /// <summary>
    /// Check whether the compiler generated a type or method rather than someone writing it
    /// </summary>
    /// <param name="member">Member to check</param>
    /// <returns>Whether it is compiler generated</returns>
    private static bool IsCompilerGenerated(ICustomAttributeProvider member)
    {
        return member.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute");
    }

    /// <summary>
    /// Surround a method's body with a Tracy zone
    /// </summary>
    /// <param name="method">Method to weave</param>
    /// <param name="index">Index of the method's source location field</param>
    /// <param name="zones">Class holding the source locations</param>
    /// <param name="register">Method registering a source location with Tracy</param>
    /// <param name="begin">Method opening a zone</param>
    /// <param name="end">Method closing a zone</param>
    private void Weave(MethodDefinition method, int index, TypeDefinition zones, MethodReference register, MethodReference begin, MethodReference end)
    {
        MethodBody body = method.Body;

        // Long form branches throughout, so that inserting instructions cannot overflow the short
        // form offsets. OptimizeMacros puts back whatever still fits once the body is complete
        body.SimplifyMacros();

        FieldDefinition location = new($"Zone{index}", FieldAttributes.Static | FieldAttributes.Assembly, ModuleDefinition.TypeSystem.IntPtr);
        zones.Fields.Add(location);

        VariableDefinition zone = new(begin.ReturnType);
        body.Variables.Add(zone);
        VariableDefinition? result = null;
        if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            result = new VariableDefinition(method.ReturnType);
            body.Variables.Add(result);
        }
        body.InitLocals = true;

        ILProcessor il = body.GetILProcessor();
        Instruction firstOriginal = body.Instructions[0];

        // Every return leaves the try instead, by way of the local the return value is parked in.
        // The existing instruction is rewritten in place rather than replaced, so that branches to
        // it still arrive at the same point
        Instruction returnValue = Instruction.Create(OpCodes.Ret);
        Instruction returnTarget = result is null ? returnValue : Instruction.Create(OpCodes.Ldloc, result);
        foreach (Instruction instruction in body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToList())
        {
            if (result is null)
            {
                instruction.OpCode = OpCodes.Leave;
                instruction.Operand = returnTarget;
            }
            else
            {
                instruction.OpCode = OpCodes.Stloc;
                instruction.Operand = result;
                il.InsertAfter(instruction, Instruction.Create(OpCodes.Leave, returnTarget));
            }
        }

        // The zone is opened before the try: a zone that was never opened must not be closed
        Instruction openZone = Instruction.Create(OpCodes.Call, begin);
        Instruction[] prologue =
        [
            // The source location is registered on the first call and remembered in the field. Two
            // threads arriving at once register it twice, which costs one unused location and is
            // cheaper than the locking needed to prevent it
            Instruction.Create(OpCodes.Ldsfld, location),
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Brtrue, openZone),
            Instruction.Create(OpCodes.Pop),
            Instruction.Create(OpCodes.Ldstr, $"{method.DeclaringType.Name}.{method.Name}"),
            Instruction.Create(OpCodes.Ldstr, $"{method.DeclaringType.FullName.Replace('/', '.')}.{method.Name}"),
            Instruction.Create(OpCodes.Call, register),
            Instruction.Create(OpCodes.Dup),
            Instruction.Create(OpCodes.Stsfld, location),
            openZone,
            Instruction.Create(OpCodes.Stloc, zone)
        ];
        foreach (Instruction instruction in prologue)
        {
            il.InsertBefore(firstOriginal, instruction);
        }

        // Closing the zone in a finally block is what makes a method that throws still leave the
        // zone stack as it found it
        Instruction closeZone = Instruction.Create(OpCodes.Ldloc, zone);
        il.Append(closeZone);
        il.Append(Instruction.Create(OpCodes.Call, end));
        il.Append(Instruction.Create(OpCodes.Endfinally));
        if (result is not null)
        {
            il.Append(returnTarget);
        }
        il.Append(returnValue);

        // Added last, so that this handler sits outside any the method already had
        body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = firstOriginal,
            TryEnd = closeZone,
            HandlerStart = closeZone,
            HandlerEnd = returnTarget
        });

        body.OptimizeMacros();
    }
}
