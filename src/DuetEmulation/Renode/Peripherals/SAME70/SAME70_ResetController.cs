//
// SAME70 Reset Controller (RSTC).
//
// Small, but not optional. CoreN2G's ResetProcessor() reboots a SAME70 with
// rstc_start_software_reset(RSTC) and then spins in for(;;){} waiting for the reset to arrive. With
// no RSTC on the bus that write is absorbed by the SVD fallback and the reset never comes, so the
// board does not reboot - it wedges in that loop with the network stack dead. The symptom is a
// machine that stops answering entirely the moment anyone clicks "reboot" in DWC or sends M999, which
// reads as a networking failure rather than as an unmodelled peripheral.
//
// Only the reset request itself is modelled. RSTC_SR reports "no reset in progress" and a RSTTYP of
// 0 (general/power-up); nothing in RepRapFirmware branches on the reset cause, so inventing a more
// faithful value would be untested guesswork.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class SAME70_ResetController : IDoubleWordPeripheral, IKnownSize
    {
        public SAME70_ResetController(IMachine machine)
        {
            this.machine = machine;
        }

        public void Reset()
        {
        }

        public long Size => 0x10;

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case StatusRegister:
                    // NRSTL high (the reset line is not asserted), SRCMP clear (no software reset in
                    // progress), RSTTYP 0. A firmware that polls SRCMP after requesting a reset must
                    // see it clear rather than spin, in the case where the reset is declined below.
                    return NrstLevel;
                case ModeRegister:
                    return mode;
                default:
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case ControlRegister:
                    if((value >> 24) != KeyPassword)
                    {
                        this.Log(LogLevel.Warning, "RSTC_CR written without the 0xA5 key ({0:X8}); ignored", value);
                        return;
                    }
                    if((value & (ProcRstBit | PerRstBit)) != 0)
                    {
                        this.Log(LogLevel.Info, "Software reset requested via RSTC_CR");
                        // Not called inline: this runs on the CPU thread, in the middle of the store
                        // instruction that asked for the reset. Resetting the machine from under it
                        // deadlocks. Deferring to the nearest synced state lets the write retire first.
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => machine.RequestReset());
                    }
                    break;

                case ModeRegister:
                    if((value >> 24) == KeyPassword)
                    {
                        mode = value & 0x00000F01;
                    }
                    break;

                default:
                    break;
            }
        }

        private uint mode;
        private readonly IMachine machine;

        private const long ControlRegister = 0x00;
        private const long StatusRegister = 0x04;
        private const long ModeRegister = 0x08;

        private const uint KeyPassword = 0xA5;
        private const uint ProcRstBit = 1u << 0;
        private const uint PerRstBit = 1u << 2;
        private const uint NrstLevel = 1u << 16;
    }
}
