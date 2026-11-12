using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DuetEmulation.Emulator;

/// <summary>
/// The XML-RPC calls Renode's robot server answers: strings, integers, booleans, doubles, arrays and
/// structs, which is all its keyword protocol uses
/// </summary>
internal sealed class XmlRpcClient(Uri endpoint, TimeSpan timeout) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = timeout };

    /// <summary>Call a method and return its result as a string, long, bool, double, array or dictionary</summary>
    /// <exception cref="XmlRpcFaultException">The server answered with a fault</exception>
    public async Task<object?> CallAsync(string method, IEnumerable<object> parameters, CancellationToken cancellationToken)
    {
        XDocument request = new(
            new XElement("methodCall",
                new XElement("methodName", method),
                new XElement("params", parameters.Select(p => new XElement("param", Encode(p))))));
        using StringContent content = new(request.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using HttpResponseMessage response = await _http.PostAsync(endpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        XDocument reply = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        XElement root = reply.Root ?? throw new FormatException("Empty XML-RPC response");

        XElement? fault = root.Element("fault");
        if (fault is not null)
        {
            Dictionary<string, object?> details = Decode(fault.Element("value")!) as Dictionary<string, object?> ?? [];
            throw new XmlRpcFaultException(details.GetValueOrDefault("faultString") as string ?? "XML-RPC fault");
        }
        XElement? value = root.Element("params")?.Element("param")?.Element("value");
        return value is null ? null : Decode(value);
    }

    private static XElement Encode(object value) => new("value", value switch
    {
        string s => new XElement("string", s),
        bool b => new XElement("boolean", b ? "1" : "0"),
        int i => new XElement("int", i.ToString(CultureInfo.InvariantCulture)),
        double d => new XElement("double", d.ToString("R", CultureInfo.InvariantCulture)),
        IEnumerable<object> items => new XElement("array", new XElement("data", items.Select(Encode))),
        _ => throw new ArgumentException($"Cannot send a {value.GetType().Name} over XML-RPC", nameof(value))
    });

    private static object? Decode(XElement value)
    {
        XElement? typed = value.Elements().FirstOrDefault();
        if (typed is null)
        {
            // A value without a type element is a string
            return value.Value;
        }
        return typed.Name.LocalName switch
        {
            "string" => typed.Value,
            "int" or "i4" or "i8" => long.Parse(typed.Value, CultureInfo.InvariantCulture),
            "boolean" => typed.Value.Trim() == "1",
            "double" => double.Parse(typed.Value, CultureInfo.InvariantCulture),
            "nil" => null,
            "array" => typed.Element("data")?.Elements("value").Select(Decode).ToList() ?? [],
            "struct" => typed.Elements("member").ToDictionary(
                m => m.Element("name")?.Value ?? string.Empty,
                m => m.Element("value") is XElement v ? Decode(v) : null),
            _ => typed.Value
        };
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>An XML-RPC call that the server answered with a fault</summary>
internal sealed class XmlRpcFaultException(string message) : Exception(message);
