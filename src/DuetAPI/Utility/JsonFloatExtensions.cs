using System.Text.Json;

namespace DuetAPI.Utility;

/// <summary>
/// Extension methods to read single-precision floats that stay serializable.
/// RepRapFirmware reports large values with few significant digits, so a value just below the float maximum may be
/// rounded beyond it. Such a number is parsed as infinity, which cannot be written as JSON again.
/// </summary>
internal static class JsonFloatExtensions
{
    /// <summary>
    /// Read a float from a JSON reader and clamp it to the finite float range
    /// </summary>
    /// <param name="reader">JSON reader</param>
    /// <returns>Finite float value</returns>
    public static float GetFiniteSingle(this ref Utf8JsonReader reader) => Clamp(reader.GetSingle());

    /// <summary>
    /// Read a float from a JSON element and clamp it to the finite float range
    /// </summary>
    /// <param name="element">JSON element</param>
    /// <returns>Finite float value</returns>
    public static float GetFiniteSingle(this JsonElement element) => Clamp(element.GetSingle());

    /// <summary>
    /// Clamp an infinite value to the finite float range
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <returns>Finite float value or NaN</returns>
    private static float Clamp(float value) => float.IsPositiveInfinity(value) ? float.MaxValue : float.IsNegativeInfinity(value) ? float.MinValue : value;
}
