using System;

namespace DuetAPI.Utility;

/// <summary>
/// Replacements for maths functions that are not available on every target framework
/// </summary>
/// <remarks>
/// <c>Math.Clamp</c> does not exist on .NET Standard 2.0, so it is reimplemented here. The
/// fallbacks match the semantics of <see cref="Math"/> exactly, including the NaN handling of the
/// floating-point overload and the exception thrown when min is greater than max.
/// </remarks>
internal static class MathCompat
{
#if NETSTANDARD2_0
    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static float Clamp(float value, float min, float max)
    {
        if (min > max)
        {
            throw new ArgumentException($"'{min}' cannot be greater than {max}", nameof(min));
        }
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static int Clamp(int value, int min, int max)
    {
        if (min > max)
        {
            throw new ArgumentException($"'{min}' cannot be greater than {max}", nameof(min));
        }
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static uint Clamp(uint value, uint min, uint max)
    {
        if (min > max)
        {
            throw new ArgumentException($"'{min}' cannot be greater than {max}", nameof(min));
        }
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static long Clamp(long value, long min, long max)
    {
        if (min > max)
        {
            throw new ArgumentException($"'{min}' cannot be greater than {max}", nameof(min));
        }
        if (value < min)
        {
            return min;
        }
        if (value > max)
        {
            return max;
        }
        return value;
    }
#else
    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static uint Clamp(uint value, uint min, uint max) => Math.Clamp(value, min, max);

    /// <summary>
    /// Clamp a value between a minimum and a maximum value
    /// </summary>
    /// <param name="value">Value to clamp</param>
    /// <param name="min">Minimum value</param>
    /// <param name="max">Maximum value</param>
    /// <returns>Value clamped between min and max</returns>
    /// <exception cref="ArgumentException">min is greater than max</exception>
    public static long Clamp(long value, long min, long max) => Math.Clamp(value, min, max);
#endif
}
