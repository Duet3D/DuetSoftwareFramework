using System.Text;

namespace DuetAPI.Utility;

/// <summary>
/// The reduced form of a name, in which case, hyphens, underscores and spaces do not count
/// </summary>
/// <remarks>
/// RepRapFirmware's <c>GCodeBuffer::GetReducedString</c>, which is how it reads sensor type names and
/// port names, so <c>Y"Linear_Analog"</c> and <c>Y"linear-analog"</c> both name <c>linearanalog</c>
/// </remarks>
public static class ReducedString
{
    /// <summary>
    /// Reduce a name: lower-case it and drop every '-', '_' and space
    /// </summary>
    /// <param name="value">Name as it was written</param>
    /// <returns>Reduced name</returns>
    public static string Reduce(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char c in value)
        {
            if (c is not ('-' or '_' or ' '))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString();
    }
}
