using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Attribute used to mark properties that change frequently (e.g. temperatures, positions, status).
/// Properties with this attribute are included when the 'f' flag is used in object model queries.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LiveAttribute : Attribute { }
