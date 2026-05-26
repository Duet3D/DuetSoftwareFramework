namespace DuetAPI.ObjectModel;

/// <summary>
/// Order in which colour components are sent to an LED strip
/// </summary>
public enum LedStripColorOrder
{
    /// <summary>
    /// Default order for DotStar LEDs
    /// </summary>
    BGR = 0,

    /// <summary>
    /// Blue, red, green
    /// </summary>
    BRG = 1,

    /// <summary>
    /// Red, green, blue
    /// </summary>
    RGB = 2,

    /// <summary>
    /// Red, blue, green
    /// </summary>
    RBG = 3,

    /// <summary>
    /// Green, blue, red
    /// </summary>
    GBR = 4,

    /// <summary>
    /// Default order for WS2812 (NeoPixel) LEDs
    /// </summary>
    GRB = 5
}
