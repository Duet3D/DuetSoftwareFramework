using System;

namespace DuetAPI.ObjectModel;

/// <summary>
/// Attribute used to mark properties that are excluded from standard object model responses.
/// Properties with this attribute are only included when the 'v' flag is used in object model queries.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class VerboseAttribute : Attribute { }
