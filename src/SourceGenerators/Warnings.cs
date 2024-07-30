using Microsoft.CodeAnalysis;

namespace DuetAPISrcGen
{
    internal static class Warnings
    {
        public static readonly DiagnosticDescriptor MissingMainOMKeyHandler = new(id: "DOM001",
                                                                                  title: "Missing key handler in main OM prop",
                                                                                  messageFormat: "Missing key handler for main OM property {0} with type {1}",
                                                                                  category: "DOM",
                                                                                  DiagnosticSeverity.Warning,
                                                                                  isEnabledByDefault: true);

    }
}
