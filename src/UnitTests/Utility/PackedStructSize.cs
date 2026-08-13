using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnitTests.Utility;

/// <summary>
/// The size of a packed struct as its fields make it, rather than as its declaration claims
/// </summary>
/// <remarks>
/// The structs that cross into native code declare <c>Size</c> so the runtime lays them out the way
/// the C++ compiler laid out their counterparts. That declaration sets the size: a struct whose
/// fields no longer fill it is padded out to it and keeps marshalling as if nothing had changed,
/// which is what makes the layout fixtures add the fields up instead of asking
/// <see cref="Marshal.SizeOf(Type)"/> and comparing it with itself
/// </remarks>
internal static class PackedStructSize
{
    /// <summary>
    /// Bytes the instance fields of a packed struct occupy
    /// </summary>
    /// <param name="type">Struct to measure</param>
    /// <returns>Size in bytes</returns>
    public static int OfFields(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Instance).Sum(OfField);

    /// <summary>
    /// Bytes one field occupies with no padding around it
    /// </summary>
    /// <param name="field">Field to measure</param>
    /// <returns>Size in bytes</returns>
    private static int OfField(FieldInfo field)
    {
        Type type = field.FieldType;
        return Marshal.SizeOf(type.IsEnum ? Enum.GetUnderlyingType(type) : type);
    }
}
