using Microsoft.CodeAnalysis;

namespace DuetAPI.SourceGenerators
{
    internal static class Descriptors
    {
        public static readonly DiagnosticDescriptor MissingMainOMKeyHandler = new(id: "DOM001",
                                                                                  title: "Missing main object model key handler",
                                                                                  messageFormat: "Missing main object model key handler for property {0} with type {1}",
                                                                                  category: "DOM",
                                                                                  DiagnosticSeverity.Warning,
                                                                                  isEnabledByDefault: true);
        public static readonly DiagnosticDescriptor UnsupportedType = new(id: "DOM002",
                                                                          title: "Unsupported type",
                                                                          messageFormat: "Cannot generate setter for property {0} of class {1} because it is not supported",
                                                                          category: "DOM",
                                                                          DiagnosticSeverity.Error,
                                                                          isEnabledByDefault: true);
        public static readonly DiagnosticDescriptor IncompleteModelObjectClass = new(id: "DOM003",
                                                                                     title: "Incomplete model class",
                                                                                     messageFormat: "Model class {0} lacks the IStaticModelObject or IDyanmicModelObject interfaces",
                                                                                     category: "DOM",
                                                                                     DiagnosticSeverity.Error,
                                                                                     isEnabledByDefault: true);

    }
}
