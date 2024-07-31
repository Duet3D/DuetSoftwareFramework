using Microsoft.CodeAnalysis;

namespace SourceGenerators
{
    internal static class Descriptors
    {
        public static readonly DiagnosticDescriptor MissingMainOMKeyHandler = new(id: "DOM001",
                                                                                  title: "Missing key handler in main OM prop",
                                                                                  messageFormat: "Missing key handler for main OM property {0} with type {1}",
                                                                                  category: "DOM",
                                                                                  DiagnosticSeverity.Warning,
                                                                                  isEnabledByDefault: true);
        public static readonly DiagnosticDescriptor UnsupportedType = new(id: "DOM002",
                                                                          title: "Unsupported type",
                                                                          messageFormat: "Cannot generate setter for property {0} of class {1} because it is not supported",
                                                                          category: "DOM",
                                                                          DiagnosticSeverity.Error,
                                                                          isEnabledByDefault: true);
    }
}
