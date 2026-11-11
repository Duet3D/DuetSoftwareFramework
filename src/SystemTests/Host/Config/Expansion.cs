namespace SystemTests.Host.Config;

/// <summary>Drivers and ports of an expansion board as config.g names them</summary>
internal static class Expansion
{
    /// <summary>A driver, e.g. 1.0</summary>
    public static string Driver(byte board, int driver) => $"{board}.{driver}";

    /// <summary>A temperature sensor input, e.g. 1.temp0</summary>
    public static string Temp(byte board, int input) => $"{board}.temp{input}";

    /// <summary>An output, e.g. 1.out3</summary>
    public static string Out(byte board, int output) => $"{board}.out{output}";

    /// <summary>An IO connector's input, e.g. 1.io0.in</summary>
    public static string GpioIn(byte board, int io) => $"{board}.io{io}.in";

    /// <summary>An IO connector's output, e.g. 1.io0.out</summary>
    public static string GpioOut(byte board, int io) => $"{board}.io{io}.out";
}
