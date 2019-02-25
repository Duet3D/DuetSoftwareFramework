namespace DuetAPI.Commands
{
    /// <summary>
    /// Instruct the control server to flush all pending commands and to wait for all moves to finish.
    /// This is similar to M400 in RepRapFirmware.
    /// </summary>
    public class Flush : Command { }
}