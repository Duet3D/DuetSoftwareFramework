//
// Bosch M_CAN with timestamps: Renode's MCAN model (renode-infrastructure at Renode 1.17.0,
// src/Emulator/Peripherals/Peripherals/CAN/MCAN.cs and MCAN_Structures.cs, MIT licensed,
// Copyright (c) 2010-2026 Antmicro) with what Duet firmware needs added. Changes are marked "Duet:".
//
// Duet firmware synchronises the expansion boards' step clocks to the main board's with CAN
// timestamps (CANlib CanMessageTimeSync; Duet3Expansion StepTimer::ProcessTimeSyncMessage), which
// Renode's model leaves at zero. This adds:
//   - the timestamp counter (TSCC, TSCV): TSS=1 counts nominal bit times divided by TCP+1, from
//     the bit timing in NBTP and the CAN clock frequency; TSS=2 reads an external counter, a
//     peripheral's "StepClock" property (the SAME70 uses TC0, its step clock, for this)
//   - the time a frame takes on the bus, from its length and the nominal and data bit timing (NBTP,
//     DBTP), ignoring stuff bits
//   - TXTS in Tx event FIFO elements: the counter when the frame has been sent, which is what
//     RepRapFirmware measures its time sync messages' transmit delay with
//   - the event type of Tx events, which Renode had wrong
//   - RXTS in Rx buffer and FIFO elements: this controller's counter when the frame finished on the
//     bus by the *sender's* clock. The hub delivers a frame at the receiver's next synchronisation
//     point, up to a quantum later; stamping it with the time it was on the bus means the sender's and
//     receivers' timestamps describe the same moment, as they do on a real bus.
//
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using Antmicro.Renode.Core.CAN;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.Packets;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.CAN
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public partial class DuetMCAN : IDoubleWordPeripheral, IKnownSize, ICAN
    {
        public DuetMCAN(IMachine machine, IMultibyteWritePeripheral messageRAM, long canClockFrequency = 48000000, IPeripheral externalTimestamp = null, long externalTimestampFrequency = 750000)
        {
            this.machine = machine;
            this.messageRAM = messageRAM;
            // Duet: timestamp counter sources
            this.canClockFrequency = canClockFrequency;
            this.externalTimestamp = externalTimestamp;
            this.externalTimestampFrequency = externalTimestampFrequency;
            externalStepClock = externalTimestamp?.GetType().GetProperty("StepClock");
            Line0 = new GPIO();
            Line1 = new GPIO();
            Calibration = new GPIO();
            BuildRegisterMapView();
            registers = new DoubleWordRegisterCollection(this, BuildRegisterMap());
            BuildStructuredViews();
            UpdateInterrupts();
        }

        public void OnFrameReceived(CANMessageFrame rxMessage)
        {
            HandleRxInner(rxMessage);
            UpdateInterrupts();
        }

        public void Reset()
        {
            registers.Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        private Dictionary<long, DoubleWordRegister> BuildRegisterMap()
        {
            var registerMap = new Dictionary<long, DoubleWordRegister>();

            registerMap[(long)Register.CoreReleaseRegister] = new DoubleWordRegister(this, resetValue: 0x33191121)
                .WithTag("DAY", 0, 8)
                .WithTag("MON", 8, 8)
                .WithTag("YEAR", 16, 4)
                .WithTag("SUBSTEP", 20, 4)
                .WithTag("STEP", 24, 4)
                .WithTag("REL", 28, 4);

            registerMap[(long)Register.EndianRegister] = new DoubleWordRegister(this, resetValue: 0x87654321)
                .WithTag("ETV", 0, 32);

            registerMap[(long)Register.CustomerRegister] = new DoubleWordRegister(this)
                .WithReservedBits(0, 32);

            // Duet: the data phase bit timing sets how long a frame with bit rate switching takes
            registerMap[(long)Register.DataBitTimingAndPrescalerRegister] = new DoubleWordRegister(this, resetValue: 0x00000A33)
                .WithValueField(0, 4, name: "DSJW")
                .WithValueField(4, 4, out dataTimeSegment2, name: "DTSEG2")
                .WithValueField(8, 5, out dataTimeSegment1, name: "DTSEG1")
                .WithReservedBits(13, 3)
                .WithValueField(16, 5, out dataPrescaler, name: "DBRP")
                .WithReservedBits(21, 2)
                .WithTaggedFlag("TDC", 23)
                .WithReservedBits(24, 8);

            registerMap[(long)Register.TestRegister] = new DoubleWordRegister(this)
                .WithReservedBits(0, 4)
                .WithFlag(4, out rv.TestRegister.LoopBackMode, changeCallback: (oldVal, newVal) =>
                {
                    if(!IsProtectedWrite)
                    {
                        this.Log(LogLevel.Warning, "Trying to write to protected field. Ignoring.");
                        rv.TestRegister.LoopBackMode.Value = oldVal;
                        return;
                    }
                    if(newVal)
                    {
                        this.NoisyLog("Loopback enabled");
                    }
                    else
                    {
                        this.NoisyLog("Loopback disabled");
                    }
                }, name: "LBCK")
                .WithTag("TX", 5, 2)
                .WithTaggedFlag("RX", 7)
                .WithValueField(8, 5, out rv.TestRegister.TxBufferNumberPrepared, FieldMode.Read, name: "TXBNP")
                .WithFlag(13, out rv.TestRegister.PreparedValid, FieldMode.Read, name: "PVAL")
                .WithReservedBits(14, 2)
                .WithValueField(16, 5, out rv.TestRegister.TxBufferNumberStarted, FieldMode.Read, name: "TXBNS")
                .WithFlag(21, out rv.TestRegister.StartedValid, FieldMode.Read, name: "SVAL")
                .WithReservedBits(22, 10);

            registerMap[(long)Register.RAMWatchdog] = new DoubleWordRegister(this)
                .WithTag("WDC", 0, 8)
                .WithTag("WDV", 8, 8)
                .WithReservedBits(16, 16);

            registerMap[(long)Register.CCControlRegister] = new DoubleWordRegister(this, resetValue: 0x1)
                .WithFlags(0, 16, out rv.CCControlRegister.ControlFields, writeCallback: (idx, oldVal, newVal) =>
                {
                    // Handle fields that have meaningful side effects from the point of emulation. Other fields are treated as flags.
                    switch((Control)idx)
                    {
                    case Control.Initialization:
                    {
                        if(!newVal)
                        {
                            rv.CCControlRegister.ControlFields[(int)Control.ConfigurationChangeEnable].Value = false;
                            this.Log(LogLevel.Debug, "Software initialization is finished");
                        }
                        break;
                    }
                    case Control.ConfigurationChangeEnable:
                    {
                        if(!rv.CCControlRegister.ControlFields[(int)Control.Initialization].Value && rv.CCControlRegister.ControlFields[(int)Control.ConfigurationChangeEnable].Value) // oldVal was reset through resetting INIT
                        {
                            rv.CCControlRegister.ControlFields[idx].Value = oldVal;
                            return;
                        }
                        if(newVal)
                        {
                            registerMap[(long)Register.HighPriorityMessageStatus].Reset();
                            registerMap[(long)Register.RxFIFO0Status].Reset();
                            registerMap[(long)Register.RxFIFO1Status].Reset();
                            registerMap[(long)Register.TxFIFOQueueStatus].Reset();
                            registerMap[(long)Register.TxBufferRequestPending].Reset();
                            registerMap[(long)Register.TxBufferTransmissionOccurred].Reset();
                            registerMap[(long)Register.TxBufferCancellationFinished].Reset();
                            registerMap[(long)Register.TxEventFIFOStatus].Reset();
                        }
                        break;
                    }
                    case Control.BusMonitoringMode:
                    case Control.TestModeEnable:
                    {
                        if(newVal)
                        {
                            if(!IsProtectedWrite && newVal != oldVal)
                            {
                                this.Log(LogLevel.Warning, "Trying to write to protected field. Ignoring.");
                                rv.CCControlRegister.ControlFields[idx].Value = oldVal;
                                return;
                            }
                        }
                        else
                        {
                            if((Control)idx == Control.TestModeEnable)
                            {
                                registerMap[(long)Register.TestRegister].Reset();
                            }
                        }
                        break;
                    }
                    case Control.DisableAutomaticRetransmission:
                    {
                        if(!IsProtectedWrite && newVal != oldVal)
                        {
                            this.Log(LogLevel.Warning, "Trying to write to protected field. Ignoring.");
                            rv.CCControlRegister.ControlFields[idx].Value = oldVal;
                            return;
                        }
                        break;
                    }
                    }
                })
                .WithReservedBits(16, 16);

            // Duet: the bit timing sets the rate of the timestamp counter
            registerMap[(long)Register.NominalBitTimingAndPrescalerRegister] = new DoubleWordRegister(this, resetValue: 0x06000A03)
                .WithValueField(0, 7, out nominalTimeSegment2, name: "NTSEG2")
                .WithReservedBits(7, 1)
                .WithValueField(8, 8, out nominalTimeSegment1, name: "NTSEG1")
                .WithValueField(16, 9, out nominalPrescaler, name: "NBRP")
                .WithValueField(25, 7, name: "NSJW");

            // Duet: the timestamp counter
            registerMap[(long)Register.TimestampCounterConfiguration] = new DoubleWordRegister(this)
                .WithValueField(0, 2, out timestampSelect, name: "TSS")
                .WithReservedBits(2, 14)
                .WithValueField(16, 4, out timestampPrescaler, name: "TCP")
                .WithReservedBits(20, 12)
                .WithWriteCallback((_, __) => timestampEpoch = Now());

            registerMap[(long)Register.TimestampCounterValue] = new DoubleWordRegister(this)
                .WithValueField(0, 16, FieldMode.Read | FieldMode.Write, valueProviderCallback: _ => TimestampAt(Now()),
                    writeCallback: (_, __) => timestampEpoch = Now(), name: "TSC")       // a write resets the internal counter
                .WithReservedBits(16, 16);

            registerMap[(long)Register.TimeoutCounterConfiguration] = new DoubleWordRegister(this, resetValue: 0xFFFF0000)
                .WithTaggedFlag("ETOC", 0)
                .WithTag("TOS", 1, 2)
                .WithReservedBits(3, 13)
                .WithTag("TOP", 16, 16);

            registerMap[(long)Register.TimeoutCounterValue] = new DoubleWordRegister(this, resetValue: 0x0000FFFF)
                .WithTag("TOC", 0, 16)
                .WithReservedBits(16, 16);

            registerMap[(long)Register.ErrorCounterRegister] = new DoubleWordRegister(this)
                .WithTag("TEC", 0, 8)
                .WithTag("REC", 8, 7)
                .WithTaggedFlag("RP", 15)
                .WithTag("CEL", 16, 8)
                .WithReservedBits(24, 8);

            registerMap[(long)Register.ProtocolStatusRegister] = new DoubleWordRegister(this, resetValue: 0x00000707)
                .WithEnumField(0, 3, out rv.ProtocolStatusRegister.LastErrorCode, readCallback: (_, __) =>
                {
                    rv.ProtocolStatusRegister.LastErrorCode.Value = LastErrorCode.NoChange; // Reset on read
                }, name: "LEC")
                .WithEnumField(3, 2, out rv.ProtocolStatusRegister.Activity, name: "ACT")
                .WithTaggedFlag("EP", 5)
                .WithTaggedFlag("EW", 6)
                .WithTaggedFlag("BO", 7)
                .WithTag("DLEC", 8, 3)
                .WithTaggedFlag("RESI", 11)
                .WithTaggedFlag("RBRS", 12)
                .WithFlag(13, out rv.ProtocolStatusRegister.ReceivedCANFDMessage, readCallback: (_, __) =>
                {
                    rv.ProtocolStatusRegister.ReceivedCANFDMessage.Value = false; // Reset on read
                }, name: "RFDF")
                .WithTaggedFlag("PXE", 14)
                .WithReservedBits(15, 1)
                .WithTag("TDCV", 16, 7)
                .WithReservedBits(23, 9);

            registerMap[(long)Register.TransmitterDelayCompensationRegister] = new DoubleWordRegister(this)
                .WithTag("TDCF", 0, 7)
                .WithReservedBits(7, 1)
                .WithTag("TDCO", 8, 7)
                .WithReservedBits(15, 17);

            registerMap[(long)Register.InterruptRegister] = new DoubleWordRegister(this)
                .WithFlags(0, 30, out rv.InterruptRegister.InterruptFlags, FieldMode.Read | FieldMode.WriteOneToClear, writeCallback: (idx, oldVal, newVal) =>
                {
                    if(!newVal)
                    {
                        return;
                    }

                    switch((Interrupt)idx)
                    {
                    case Interrupt.RxFIFO0MessageLost:
                    {
                        rxFIFO0.MessageLost.Value = false;
                        break;
                    }
                    case Interrupt.RxFIFO1MessageLost:
                    {
                        rxFIFO1.MessageLost.Value = false;
                        break;
                    }
                    case Interrupt.TxEventFIFOElementLost:
                    {
                        txEventFIFO.ElementLost.Value = false;
                        break;
                    }
                    }
                })
                .WithWriteCallback((_, __) => UpdateInterrupts());

            registerMap[(long)Register.InterruptEnable] = new DoubleWordRegister(this)
                .WithFlags(0, 30, out rv.InterruptEnable.InterruptEnableFlags)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            registerMap[(long)Register.InterruptLineSelect] = new DoubleWordRegister(this)
                .WithFlags(0, 30, out rv.InterruptLineSelect.InterruptLineSelectFlags)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            registerMap[(long)Register.InterruptLineEnable] = new DoubleWordRegister(this)
                .WithFlag(0, out rv.InterruptLineEnable.EnableInterruptLine0, name: "EINT0")
                .WithFlag(1, out rv.InterruptLineEnable.EnableInterruptLine1, name: "EINT1")
                .WithWriteCallback((_, __) => UpdateInterrupts());

            registerMap[(long)Register.GlobalFilterConfiguration] = new DoubleWordRegister(this)
                .WithFlag(0, out rv.GlobalFilterConfiguration.RejectRemoteFramesExtended, name: "RRFE")
                .WithFlag(1, out rv.GlobalFilterConfiguration.RejectRemoteFramesStandard, name: "RRFS")
                .WithEnumField(2, 2, out rv.GlobalFilterConfiguration.AcceptNonMatchingFramesExtended, name: "ANFE")
                .WithEnumField(4, 2, out rv.GlobalFilterConfiguration.AcceptNonMatchingFramesStandard, name: "ANFS");

            registerMap[(long)Register.StandardIDFilterConfiguration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.StandardIDFilterConfiguration.FilterListStandardStartAddress, name: "FLSSA")
                .WithValueField(16, 8, out rv.StandardIDFilterConfiguration.ListSizeStandard, name: "LSS")
                .WithReservedBits(24, 8);

            registerMap[(long)Register.ExtendedIDFilterConfiguration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.ExtendedIDFilterConfiguration.FilterListExtendedStartAddress, name: "FLESA")
                .WithValueField(16, 7, out rv.ExtendedIDFilterConfiguration.ListSizeExtended, name: "LSE")
                .WithReservedBits(23, 9);

            registerMap[(long)Register.ExtendedIdANDMask] = new DoubleWordRegister(this, resetValue: 0x1FFFFFFF)
                .WithValueField(0, 29, out rv.ExtendedIdANDMask.ExtendedIDANDMask, name: "EIDM")
                .WithReservedBits(29, 3);

            registerMap[(long)Register.HighPriorityMessageStatus] = new DoubleWordRegister(this)
                .WithValueField(0, 6, valueField: out rv.HighPriorityMessageStatus.BufferIndex, name: "BIDX")
                .WithEnumField(6, 2, out rv.HighPriorityMessageStatus.MessageStorageIndicator, name: "MSI")
                .WithValueField(8, 7, out rv.HighPriorityMessageStatus.FilterIndex, name: "FIDX")
                .WithFlag(15, out rv.HighPriorityMessageStatus.FilterList, name: "FLST")
                .WithReservedBits(16, 16);

            registerMap[(long)Register.NewData1] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.NewData1.NewData1Flags, FieldMode.Read | FieldMode.WriteOneToClear, name: "newData1x");

            registerMap[(long)Register.NewData2] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.NewData2.NewData2Flags, FieldMode.Read | FieldMode.WriteOneToClear, name: "newData2x");

            registerMap[(long)Register.RxFIFO0Configuration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.RxFIFO0Configuration.RxFIFO0StartAddress, name: "F0SA")
                .WithValueField(16, 7, out rv.RxFIFO0Configuration.RxFIFO0Size, name: "F0S")
                .WithReservedBits(23, 1)
                .WithValueField(24, 7, out rv.RxFIFO0Configuration.RxFIFO0Watermark, name: "F0WM")
                .WithEnumField(31, 1, out rv.RxFIFO0Configuration.FIFO0OperationMode, name: "F0OM");

            registerMap[(long)Register.RxFIFO0Status] = new DoubleWordRegister(this)
                .WithValueField(0, 7, FieldMode.Read, valueProviderCallback: _ => rxFIFO0.RxFIFOFillLevel, name: "F0FL")
                .WithReservedBits(7, 1)
                .WithValueField(8, 6, out rv.RxFIFO0Status.RxFIFO0GetIndex, FieldMode.Read, name: "F0GI")
                .WithReservedBits(14, 2)
                .WithValueField(16, 6, out rv.RxFIFO0Status.RxFIFO0PutIndex, FieldMode.Read, name: "F0PI")
                .WithReservedBits(22, 2)
                .WithFlag(24, out rv.RxFIFO0Status.RxFIFO0Full, FieldMode.Read, name: "F0F")
                .WithFlag(25, out rv.RxFIFO0Status.RxFIFO0MessageLost, FieldMode.Read, name: "RF0L")
                .WithReservedBits(26, 6);

            registerMap[(long)Register.RxFIFO0Acknowledge] = new DoubleWordRegister(this)
                .WithValueField(0, 6, out rv.RxFIFO0Acknowledge.RxFIFO0AcknowledgeIndex, writeCallback: (_, newVal) =>
                {
                    rxFIFO0.RxFIFOGetIndex = newVal + 1;
                    rxFIFO0.Full.Value = false;
                }, name: "F0AI")
                .WithReservedBits(6, 26);

            registerMap[(long)Register.RxBufferConfiguration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.RxBufferConfiguration.RxBufferStartAddress, name: "RBSA")
                .WithReservedBits(16, 16);

            registerMap[(long)Register.RxFIFO1Configuration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.RxFIFO1Configuration.RxFIFO1StartAddress, name: "F1SA")
                .WithValueField(16, 7, out rv.RxFIFO1Configuration.RxFIFO1Size, name: "F1S")
                .WithReservedBits(23, 1)
                .WithValueField(24, 7, out rv.RxFIFO1Configuration.RxFIFO1Watermark, name: "F1WM")
                .WithEnumField(31, 1, out rv.RxFIFO1Configuration.FIFO1OperationMode, name: "F1OM");

            registerMap[(long)Register.RxFIFO1Status] = new DoubleWordRegister(this)
                .WithValueField(0, 7, FieldMode.Read, valueProviderCallback: _ => rxFIFO1.RxFIFOFillLevel, name: "F0FL")
                .WithReservedBits(7, 1)
                .WithValueField(8, 6, out rv.RxFIFO1Status.RxFIFO1GetIndex, FieldMode.Read, name: "F0GI")
                .WithReservedBits(14, 2)
                .WithValueField(16, 6, out rv.RxFIFO1Status.RxFIFO1PutIndex, FieldMode.Read, name: "F0PI")
                .WithReservedBits(22, 2)
                .WithFlag(24, out rv.RxFIFO1Status.RxFIFO1Full, FieldMode.Read, name: "F0F")
                .WithFlag(25, out rv.RxFIFO1Status.RxFIFO1MessageLost, FieldMode.Read, name: "RF0L")
                .WithReservedBits(26, 4)
                .WithEnumField(30, 2, out rv.RxFIFO1Status.DebugMessageStatus, FieldMode.Read, name: "DMS");

            registerMap[(long)Register.RxFIFO1Acknowledge] = new DoubleWordRegister(this)
                .WithValueField(0, 6, out rv.RxFIFO1Acknowledge.RxFIFO1AcknowledgeIndex, writeCallback: (_, newVal) =>
                {
                    rxFIFO1.RxFIFOGetIndex = newVal + 1;
                    rxFIFO1.Full.Value = false;
                }, name: "F1AI")
                .WithReservedBits(6, 26);

            registerMap[(long)Register.RxBufferFIFOElementSizeConfiguration] = new DoubleWordRegister(this)
                .WithValueField(0, 3, out rv.RxBufferFIFOElementSizeConfiguration.RxFIFO0DataFieldSize, name: "F0DS")
                .WithReservedBits(3, 1)
                .WithValueField(4, 3, out rv.RxBufferFIFOElementSizeConfiguration.RxFIFO1DataFieldSize, name: "F1DS")
                .WithReservedBits(7, 1)
                .WithValueField(8, 3, out rv.RxBufferFIFOElementSizeConfiguration.RxBufferDataFieldSize, name: "RBDS")
                .WithReservedBits(11, 21);

            registerMap[(long)Register.TxBufferConfiguration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.TxBufferConfiguration.TxBuffersStartAddress, name: "TBSA") // start address is aligned to four bytes, hence the lowest two bits of register are reserved
                .WithValueField(16, 6, out rv.TxBufferConfiguration.NumberOfDedicatedTxBuffers, name: "NDTB")
                .WithReservedBits(22, 2)
                .WithValueField(24, 6, out rv.TxBufferConfiguration.TxFIFOQueueSize, name: "TFQS")
                .WithFlag(30, out rv.TxBufferConfiguration.TxFIFOQueueMode, name: "TFQM")
                .WithReservedBits(31, 1);

            registerMap[(long)Register.TxFIFOQueueStatus] = new DoubleWordRegister(this)
                .WithValueField(0, 6, FieldMode.Read, valueProviderCallback: _ => txFIFOQueue.FreeLevel, name: "TFFL")
                .WithReservedBits(6, 2)
                .WithValueField(8, 5, out rv.TxFIFOQueueStatus.TxFIFOGetIndex, FieldMode.Read, valueProviderCallback: _ => txFIFOQueue.GetIndex, name: "TFGI")
                .WithReservedBits(13, 3)
                .WithValueField(16, 5, out rv.TxFIFOQueueStatus.TxFIFOQueuePutIndex, FieldMode.Read, valueProviderCallback: _ => txFIFOQueue.PutIndex, name: "TFQPI")
                .WithFlag(21, out rv.TxFIFOQueueStatus.TxFIFOQueueFull, FieldMode.Read, valueProviderCallback: _ => txFIFOQueue.Full, name: "TFQF")
                .WithReservedBits(22, 10);

            registerMap[(long)Register.TxBufferElementSizeConfiguration] = new DoubleWordRegister(this)
                .WithValueField(0, 3, out rv.TxBufferElementSizeConfiguration.TxBufferDataFieldSize, name: "TBDS")
                .WithReservedBits(3, 29);

            registerMap[(long)Register.TxBufferRequestPending] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferRequestPending.TransmissionRequestPendingFlags, FieldMode.Read, name: "TRPx");

            registerMap[(long)Register.TxBufferAddRequest] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferAddRequest.AddRequestFlags, writeCallback: (idx, oldVal, newVal) =>
                {
                    if(!newVal || rv.CCControlRegister.ControlFields[(int)Control.ConfigurationChangeEnable].Value || idx >= (int)(rv.TxBufferConfiguration.NumberOfDedicatedTxBuffers.Value + rv.TxBufferConfiguration.TxFIFOQueueSize.Value))
                    {
                        rv.TxBufferAddRequest.AddRequestFlags[idx].Value = oldVal;
                        return;
                    }
                    rv.TxBufferRequestPending.TransmissionRequestPendingFlags[idx].Value = true;
                    rv.TxBufferCancellationFinished.CancellationFinishedFlags[idx].Value = false;
                    rv.TxBufferTransmissionOccurred.TransmissionOccurredFlags[idx].Value = false;

                    if(!txFIFOQueue.QueueMode.Value && idx >= (int)txFIFOQueue.Offset.Value)
                    {
                        // Message added Tx FIFO
                        txFIFOQueue.PutIndex++;
                        if(txFIFOQueue.PutIndex == txFIFOQueue.GetIndex)
                        {
                            txFIFOQueue.FullRaw.Value = true;
                            this.Log(LogLevel.Warning, "Tx FIFO is full");
                        }
                    }
                }, name: "ARx")
                .WithWriteCallback((oldVal, newVal) =>
                {
                    TxScan();
                });

            registerMap[(long)Register.TxBufferCancellationRequest] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferCancellationRequest.CancellationRequestFlags, writeCallback: (idx, oldVal, newVal) =>
                {
                    if(!newVal || rv.CCControlRegister.ControlFields[(int)Control.ConfigurationChangeEnable].Value || idx >= (int)(rv.TxBufferConfiguration.NumberOfDedicatedTxBuffers.Value + rv.TxBufferConfiguration.TxFIFOQueueSize.Value))
                    {
                        rv.TxBufferCancellationRequest.CancellationRequestFlags[idx].Value = oldVal;
                        return;
                    }
                    rv.TxBufferRequestPending.TransmissionRequestPendingFlags[idx].Value = false;
                    rv.TxBufferCancellationRequest.CancellationRequestFlags[idx].Value = false;

                    if(!txFIFOQueue.QueueMode.Value && idx == (int)txFIFOQueue.GetIndex)
                    {
                        txFIFOQueue.GetIndex++;
                        txFIFOQueue.FullRaw.Value = false;
                    }

                    rv.TxBufferCancellationFinished.CancellationFinishedFlags[idx].Value = true;
                    rv.InterruptRegister.InterruptFlags[(int)Interrupt.TransmissionCancellationFinished].Value = true;
                }, name: "CRx")
                .WithWriteCallback((oldVal, newVal) =>
                {
                    UpdateInterrupts();
                    TxScan();
                });

            registerMap[(long)Register.TxBufferTransmissionOccurred] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferTransmissionOccurred.TransmissionOccurredFlags, FieldMode.Read, name: "TOx");

            registerMap[(long)Register.TxBufferCancellationFinished] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferCancellationFinished.CancellationFinishedFlags, FieldMode.Read, name: "CFx");

            registerMap[(long)Register.TxBufferTransmissionInterruptEnable] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferTransmissionInterruptEnable.TransmissionInterruptEnableFlags, name: "TIEx");

            registerMap[(long)Register.TxBufferCancellationFinishedInterruptEnable] = new DoubleWordRegister(this)
                .WithFlags(0, 32, out rv.TxBufferCancellationFinishedInterruptEnable.CancellationFinishedInterruptEnableFlags, name: "CFIEx");

            registerMap[(long)Register.TxEventFIFOConfiguration] = new DoubleWordRegister(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out rv.TxEventFIFOConfiguration.EventFIFOStartAddress, name: "EFSA")
                .WithValueField(16, 6, out rv.TxEventFIFOConfiguration.EventFIFOSize, name: "EFS")
                .WithReservedBits(22, 2)
                .WithValueField(24, 6, out rv.TxEventFIFOConfiguration.EventFIFOWatermark, name: "EFWM")
                .WithReservedBits(30, 2);

            registerMap[(long)Register.TxEventFIFOStatus] = new DoubleWordRegister(this)
                .WithValueField(0, 6, FieldMode.Read, valueProviderCallback: _ => txEventFIFO.FillLevel, name: "EFFL")
                .WithReservedBits(6, 2)
                .WithValueField(8, 5, out rv.TxEventFIFOStatus.EventFIFOGetIndex, FieldMode.Read, name: "EFGI")
                .WithReservedBits(13, 3)
                .WithValueField(16, 5, out rv.TxEventFIFOStatus.EventFIFOPutIndex, FieldMode.Read, name: "EFPI")
                .WithReservedBits(21, 3)
                .WithFlag(24, out rv.TxEventFIFOStatus.EventFIFOFull, FieldMode.Read, name: "EFF")
                .WithFlag(25, out rv.TxEventFIFOStatus.TxEventFIFOElementLost, FieldMode.Read, name: "TEFL")
                .WithReservedBits(26, 6);

            registerMap[(long)Register.TxEventFIFOAcknowledge] = new DoubleWordRegister(this)
                .WithValueField(0, 5, out rv.TxEventFIFOAcknowledge.EventFIFOAcknowledgeIndex, writeCallback: (_, newVal) =>
                {
                    txEventFIFO.GetIndex = newVal + 1;
                    txEventFIFO.Full.Value = false;
                    if(txEventFIFO.FillLevel == 0)
                    {
                        rv.InterruptRegister.InterruptFlags[(int)Interrupt.TxFIFOEmpty].Value = true;
                        UpdateInterrupts();
                    }
                }, name: "EFAI")
                .WithReservedBits(5, 27);

            return registerMap;
        }

        private void TxScan()
        {
            var bufferIdx = FindPrioritizedBuffer(out var messageID);
            if(bufferIdx == -1)
            {
                return;
            }
            this.Log(LogLevel.Debug, "Buffer {0} with Message ID {1} will be transmitted", bufferIdx, messageID);
            TransmitBuffer(bufferIdx);
        }

        // It scans Tx Buffers section in the Message RAM and returns index of the buffer to be transmitted next according to Tx prioritization rules.
        private int FindPrioritizedBuffer(out uint messageID)
        {
            var numberOfDedicatedTxBuffers = txBuffers.NumberOfDedicatedTxBuffers.Value;
            var txFIFOQueueSize = txBuffers.TxFIFOQueueSize.Value;
            var txFIFOQueueMode = txBuffers.TxFIFOQueueMode.Value;

            var txScanMode = TxScanModeInternal.Dedicated;

            if(txFIFOQueueSize == 0)
            {
                txScanMode = TxScanModeInternal.Dedicated;
            }
            else if(numberOfDedicatedTxBuffers == 0)
            {
                if(txFIFOQueueMode)
                {
                    txScanMode = TxScanModeInternal.Queue;
                }
                else
                {
                    txScanMode = TxScanModeInternal.FIFO;
                }
            }
            else
            {
                if(txFIFOQueueMode)
                {
                    txScanMode = TxScanModeInternal.MixedDedicatedQueue;
                }
                else
                {
                    txScanMode = TxScanModeInternal.MixedDedicatedFIFO;
                }
            }

            messageID = uint.MaxValue; // Message ID of selected buffer
            var bufferNumber = -1; // Index of selected buffer

            switch(txScanMode)
            {
            case TxScanModeInternal.Dedicated:
            {
                bufferNumber = ScanDedicatedTxBuffers(out messageID);
                break;
            }
            case TxScanModeInternal.FIFO:
            {
                bufferNumber = ScanTxFIFO(out messageID);
                break;
            }
            case TxScanModeInternal.Queue:
            {
                bufferNumber = ScanTxQueue(out messageID);
                break;
            }
            case TxScanModeInternal.MixedDedicatedFIFO:
            {
                var bufferNumber0 = ScanDedicatedTxBuffers(out var messageID0);
                var bufferNumber1 = ScanTxFIFO(out var messageID1);
                messageID = messageID0 <= messageID1 ? messageID0 : messageID1;
                bufferNumber = messageID0 <= messageID1 ? bufferNumber0 : bufferNumber1;
                break;
            }
            case TxScanModeInternal.MixedDedicatedQueue:
            {
                var bufferNumber0 = ScanDedicatedTxBuffers(out var messageID0);
                var bufferNumber1 = ScanTxQueue(out var messageID1);
                messageID = messageID0 <= messageID1 ? messageID0 : messageID1;
                bufferNumber = messageID0 <= messageID1 ? bufferNumber0 : bufferNumber1;
                break;
            }
            }

            return bufferNumber;
        }

        private int ScanDedicatedTxBuffers(out uint messageID)
        {
            var startAddress = (int)(txBuffers.StartAddress.Value << 2);
            var dataSizeInBytes = MapDataFieldSizeToDataBytes(txBuffers.DataFieldSize.Value);
            var bufferSizeInBytes = BufferElementHeaderSizeInBytes + dataSizeInBytes;

            messageID = uint.MaxValue; // Message ID of selected buffer
            var bufferNumber = -1; // Index of selected buffer

            for(var i = 0; i < (int)txBuffers.NumberOfDedicatedTxBuffers.Value; i++)
            {
                if(!rv.TxBufferRequestPending.TransmissionRequestPendingFlags[i].Value)
                {
                    continue;
                }

                var offset = startAddress + i * bufferSizeInBytes;
                byte[] scanBytes = messageRAM.ReadBytes((long)offset, 4);
                var scanRecord = Packet.Decode<TxScanBufferAndEventFIFOCommonHeader>(scanBytes);

                var id = scanRecord.Identifier;
                id = scanRecord.ExtendedIdentifier ? id : (id >> 18);

                if(id < messageID || (i == 0 && id == uint.MaxValue))
                {
                    // Found a message with higher priority
                    messageID = id;
                    bufferNumber = i;
                }
            }

            return bufferNumber;
        }

        private int ScanTxFIFO(out uint messageID)
        {
            var startAddress = (int)(txBuffers.StartAddress.Value << 2);
            var dataSizeInBytes = MapDataFieldSizeToDataBytes(txBuffers.DataFieldSize.Value);
            var bufferSizeInBytes = BufferElementHeaderSizeInBytes + dataSizeInBytes;

            messageID = uint.MaxValue; // Message ID of selected buffer
            var bufferNumber = -1; // Index of selected buffer

            var getIndex = (int)txFIFOQueue.GetIndex;

            if(!rv.TxBufferRequestPending.TransmissionRequestPendingFlags[getIndex].Value)
            {
                return bufferNumber;
            }

            var offset = startAddress + getIndex * bufferSizeInBytes;
            byte[] scanBytes = messageRAM.ReadBytes((long)offset, 4);
            var scanRecord = Packet.Decode<TxScanBufferAndEventFIFOCommonHeader>(scanBytes);

            var id = scanRecord.Identifier;
            id = scanRecord.ExtendedIdentifier ? id : (id >> 18);

            messageID = id;
            bufferNumber = getIndex;

            return bufferNumber;
        }

        private int ScanTxQueue(out uint messageID)
        {
            var startAddress = (int)(txBuffers.StartAddress.Value << 2);
            var dataSizeInBytes = MapDataFieldSizeToDataBytes(txBuffers.DataFieldSize.Value);
            var bufferSizeInBytes = BufferElementHeaderSizeInBytes + dataSizeInBytes;

            messageID = uint.MaxValue; // Message ID of selected buffer
            var bufferNumber = -1; // Index of selected buffer

            for(var i = (int)txFIFOQueue.Offset.Value; i < (int)(txFIFOQueue.Offset.Value + txFIFOQueue.Size.Value); i++)
            {
                if(!rv.TxBufferRequestPending.TransmissionRequestPendingFlags[i].Value)
                {
                    continue;
                }

                var offset = startAddress + i * bufferSizeInBytes;
                byte[] scanBytes = messageRAM.ReadBytes((long)offset, 4);
                var scanRecord = Packet.Decode<TxScanBufferAndEventFIFOCommonHeader>(scanBytes);

                var id = scanRecord.Identifier;
                id = scanRecord.ExtendedIdentifier ? id : (id >> 18);

                if(id < messageID || (i == 0 && id == uint.MaxValue))
                {
                    // Found a message with higher priority
                    messageID = id;
                    bufferNumber = i;
                }
            }

            return bufferNumber;
        }

        private void TransmitBuffer(int i)
        {
            var txBufferDataSize = MapDataFieldSizeToDataBytes(rv.TxBufferElementSizeConfiguration.TxBufferDataFieldSize.Value);
            var bufferSizeInBytes = BufferElementHeaderSizeInBytes + txBufferDataSize;

            if(!rv.TxBufferRequestPending.TransmissionRequestPendingFlags[i].Value)
            {
                return; // it was cancelled
            }

            var addr = (rv.TxBufferConfiguration.TxBuffersStartAddress.Value << 2) + (ulong)(i * bufferSizeInBytes);
            var frameBytes = messageRAM.ReadBytes((long)addr, (int)bufferSizeInBytes);
            var frame = Packet.Decode<TxBufferElementHeader>(frameBytes);
            var data = frameBytes.Skip(BufferElementHeaderSizeInBytes).Take((int)txBufferDataSize).ToArray();

            var dlcBufferSizeInBytes = MapDataLengthCodeToDataBytesCount(frame.DataLengthCode);
            Array.Resize(ref data, dlcBufferSizeInBytes);

            if(txBufferDataSize < dlcBufferSizeInBytes)
            {
                // The bytes not defined by the Tx Buffer are transmitted as “0xCC” (padding bytes).
                for(var j = (int)txBufferDataSize; j < dlcBufferSizeInBytes; j++)
                {
                    data[j] = PaddingByte;
                }
            }

            var canMessage = CANMessageFrame.CreateWithExtendedId(frame.Identifier, data, frame.ExtendedIdentifier, frame.RemoteTransmissionRequest, frame.FDFormat, frame.BitRateSwitch);

            var wasTransmitted = TransmitMessage(canMessage);
            if(wasTransmitted)
            {
                HandleTransmitSuccess(i, frame);
            }

            machine.LocalTimeSource.ExecuteInNearestSyncedState(__ =>
            {
                if(wasTransmitted)
                {
                    UpdateInterrupts();
                    TxScan();
                }
            }, true);
        }

        private void HandleTransmitSuccess(int i, TxBufferElementHeader frame)
        {
            rv.TxBufferRequestPending.TransmissionRequestPendingFlags[i].Value = false;
            rv.ProtocolStatusRegister.LastErrorCode.Value = LastErrorCode.NoError;
            HandleTxEvent(frame);

            if(!txFIFOQueue.QueueMode.Value && i >= (int)txFIFOQueue.Offset.Value)
            {
                // Transmitted message belonged to Tx FIFO
                txFIFOQueue.GetIndex++;
                txFIFOQueue.FullRaw.Value = false;
            }

            rv.TxBufferTransmissionOccurred.TransmissionOccurredFlags[i].Value = true;
            rv.InterruptRegister.InterruptFlags[(int)Interrupt.TransmissionCompleted].Value = true;
        }

        private void HandleTxEvent(TxBufferElementHeader txHeader)
        {
            if(rv.CCControlRegister.ControlFields[(int)Control.WideMessageMarker].Value)
            {
                this.Log(LogLevel.Warning, "Wide Message Marker requires an external Time Stamping Unit (TSU) that is not available");
                return;
            }

            var txFIFOElement = new TxEventFIFOElement
            {
                Identifier = txHeader.Identifier,
                RemoteTransmissionRequest = txHeader.RemoteTransmissionRequest,
                ExtendedIdentifier = txHeader.ExtendedIdentifier,
                ErrorStateIndicator = txHeader.ErrorStateIndicator,
                TxTimestamp = TimestampAt(lastTransmissionEnd),  // Duet
                DataLengthCode = txHeader.DataLengthCode,
                BitRateSwitch = txHeader.BitRateSwitch,
                FDFormat = txHeader.FDFormat,
                EventType = 0b01, // Duet: Tx event. Renode had 0b10, which is "transmission in spite of cancellation" (M_CAN user manual, Tx Event FIFO element), and CoreN2G ignores those
                MessageMarker = txHeader.MessageMarkerLow
            };

            StoreInTxEventFIFO(txFIFOElement);
        }

        private void StoreInTxEventFIFO(TxEventFIFOElement txFIFOElement)
        {
            if(txEventFIFO.Full.Value || txEventFIFO.Size.Value == 0)
            {
                txEventFIFO.InterruptElementLost.Value = true;
                txEventFIFO.ElementLost.Value = true;
                return;
            }

            var addr = (txEventFIFO.StartAddress.Value << 2) + txEventFIFO.PutIndex * (ulong)TxEventFIFOElementSizeInBytes;
            var txFIFOElementBytes = Packet.Encode(txFIFOElement);

            messageRAM.WriteBytes((long)addr, txFIFOElementBytes, 0, txFIFOElementBytes.Length);

            txEventFIFO.PutIndex += 1;
            txEventFIFO.Full.Value = txEventFIFO.PutIndexRaw == txEventFIFO.GetIndexRaw;

            var watermarkReached = (txEventFIFO.Watermark.Value > 0) && (txEventFIFO.FillLevel >= txEventFIFO.Watermark.Value);
            if(watermarkReached)
            {
                txEventFIFO.InterruptWatermarkReached.Value = true;
            }

            if(txEventFIFO.Full.Value)
            {
                txEventFIFO.InterruptFull.Value = true;
            }

            txEventFIFO.InterruptNewEntry.Value = true;
        }

        private bool TransmitMessage(CANMessageFrame canMessage)
        {
            var transmitted = false;
            var fs = FrameSent;
            this.NoisyLog("Sent CAN message {0}", canMessage);

            if(rv.TestRegister.LoopBackMode.Value)
            {
                transmitted = true;
                this.NoisyLog("Loopbacked message");
                LoopbackReceive(canMessage);
            }

            if(fs != null)
            {
                transmitted = true;
                RecordSendingTime(canMessage);                   // Duet: before the receivers see it
                fs.Invoke(canMessage);
            }
            if(!transmitted)
            {
                this.Log(LogLevel.Warning, "FrameSent is not initialized. Is the controller connected to medium?");
            }

            return transmitted;
        }

        private void HandleRxInner(CANMessageFrame rxMessage)
        {
            this.NoisyLog("Received CAN message {0}", rxMessage);
            var match = FilterMessage(rxMessage);
            if(!match)
            {
                this.Log(LogLevel.Debug, "Received CAN message discarded");
            }
        }

        private bool FilterMessage(CANMessageFrame rxMessage)
        {
            var isExtended = rxMessage.ExtendedFormat;
            var isRemote = rxMessage.RemoteFrame;
            var id = rxMessage.Id;

            rv.ProtocolStatusRegister.ReceivedCANFDMessage.Value = rxMessage.FDFormat;

            var filterConfig = isExtended ? filterConfigurationExtended : filterConfigurationStandard;

            var rejectRemoteFrames = filterConfig.RejectRemoteFrames.Value;
            if(isRemote && rejectRemoteFrames)
            {
                return false; // discard remote frame
            }

            var listSize = filterConfig.ListSize.Value;
            var receiveFilterListEnabled = listSize > 0;

            var acceptNonMatchingFrames = filterConfig.AcceptNonMatchingFrames.Value;
            var rejectNonMatchingFrames = acceptNonMatchingFrames != NonMatchingFrameTarget.AcceptInRxFIFO0 && acceptNonMatchingFrames != NonMatchingFrameTarget.AcceptInRxFIFO1;
            if(!receiveFilterListEnabled && rejectNonMatchingFrames)
            {
                return false; // discard non-matching frame
            }

            if(receiveFilterListEnabled)
            {
                var startAddress = filterConfig.FilterListStartAddress.Value;
                var filterSizeInBytes = isExtended ? ExtendedFilterSizeInBytes : StandardFilterSizeInBytes;

                var match = false;
                for(var i = 0; i < (int)listSize; i++)
                {
                    var addr = (long)(startAddress << 2) + i * filterSizeInBytes;
                    var filterBytes = messageRAM.ReadBytes(addr, filterSizeInBytes);
                    var filter = DecodeFilterElement(isExtended, filterBytes);

                    if(filter.FilterElementConfiguration == FilterElementConfiguration.DisableFilter)
                    {
                        continue; // filter element disabled
                    }
                    else if(filter.FilterElementConfiguration == FilterElementConfiguration.RxBufferOrDebugMessageOnMatch)
                    {
                        var rxBufferIdx = BitHelper.GetValue(filter.ID2, 0, 6);
                        var newDataFlags = rxBufferIdx < 32 ? rv.NewData1.NewData1Flags : rv.NewData2.NewData2Flags;
                        if(newDataFlags[rxBufferIdx % 32].Value)
                        {
                            // While an Rx Buffer’s New Data flag is set, a Message ID Filter Element
                            // referencing this specific RxBuffer will not match.
                            continue;
                        }
                        // M_CAN: a filter element set to "store into Rx Buffer or as a debug
                        // message" (SFEC/EFEC == 7) ignores the SFT/EFT filter-type field and
                        // matches the dedicated ID in ID1 (SFID1/EFID1). ID2 (SFID2/EFID2) holds
                        // the target Rx Buffer index in this mode, not a second ID, so it must
                        // not be handed to the range/dual/classic matcher.
                        // For extended frames, the received ID is ANDed with the Extended ID
                        // AND Mask (XIDAM) before the comparison, as acceptance filtering of
                        // extended frames is always performed on the masked ID.
                        var effectiveId = isExtended
                            ? id & (uint)rv.ExtendedIdANDMask.ExtendedIDANDMask.Value
                            : id;
                        match = effectiveId == filter.ID1;
                    }
                    else
                    {
                        match = MatchFilterElement(isExtended, id, filter);
                    }

                    if(!match)
                    {
                        continue;
                    }

                    HandleMatchedFilter(filter, i, rxMessage);

                    return true;
                }

                if(!match && rejectNonMatchingFrames)
                {
                    return false; // discard non-matching frame
                }
            }

            AcceptNonMatchingFrame(acceptNonMatchingFrames, rxMessage);

            return true;
        }

        private MessageIDFilterElement DecodeFilterElement(bool extended, byte[] filterBytes)
        {
            if(extended)
            {
                var filter = Packet.Decode<ExtendedMessageIDFilterElement>(filterBytes);
                return new MessageIDFilterElement
                {
                    ID1 = filter.ExtendedFilterID1,
                    ID2 = filter.ExtendedFilterID2,
                    SyncMessage = filter.ExtendedSyncMessage,
                    FilterElementConfiguration = filter.ExtendedFilterElementConfiguration,
                    FilterType = filter.ExtendedFilterType,
                    IsExtended = true
                };
            }
            else
            {
                var filter = Packet.Decode<StandardMessageIDFilterElement>(filterBytes);
                return new MessageIDFilterElement
                {
                    ID1 = filter.StandardFilterID1,
                    ID2 = filter.StandardFilterID2,
                    SyncMessage = filter.StandardSyncMessage,
                    FilterElementConfiguration = filter.StandardFilterElementConfiguration,
                    FilterType = filter.StandardFilterType,
                    IsExtended = false
                };
            }
        }

        private bool MatchFilterElement(bool isExtended, uint id, MessageIDFilterElement filter)
        {
            switch(filter.FilterType)
            {
            case FilterType.Range:
            {
                if(!isExtended)
                {
                    if(id >= filter.ID1 && id <= filter.ID2)
                    {
                        return true;
                    }
                }
                else
                {
                    var idMasked = id & (uint)rv.ExtendedIdANDMask.ExtendedIDANDMask.Value;
                    if(idMasked >= filter.ID1 && idMasked <= filter.ID2)
                    {
                        return true;
                    }
                }
                break;
            }
            case FilterType.DualID:
            {
                if((id == filter.ID1) || (id == filter.ID2))
                {
                    return true;
                }
                break;
            }
            case FilterType.Classic:
            {
                if((id & filter.ID2) == (filter.ID1 & filter.ID2))
                {
                    return true;
                }
                break;
            }
            case FilterType.RangeWithoutMask:
            {
                if(!isExtended)
                {
                    break;
                }
                if(id >= filter.ID1 && id <= filter.ID2)
                {
                    return true;
                }
                break;
            }
            default:
            {
                this.Log(LogLevel.Warning, "Invalid filter type.");
                break;
            }
            }
            return false;
        }

        private void HandleMatchedFilter(MessageIDFilterElement filter, int idx, CANMessageFrame rxMessage)
        {
            var rxBufferElementHeader = new RxBufferElementHeader
            {
                Identifier = rxMessage.ExtendedId,
                RemoteTransmissionRequest = rxMessage.RemoteFrame,
                ExtendedIdentifier = rxMessage.ExtendedFormat,
                ErrorStateIndicator = false,
                RxTimestamp = TimestampAt(SentAt(rxMessage)),   // Duet
                DataLengthCode = MapDataBytesCountToDataLengthCode(k: rxMessage.Data.Length),
                BitRateSwitch = rxMessage.BitRateSwitch,
                FDFormat = rxMessage.FDFormat,
                FilterIndex = (byte)idx,
                AcceptedNonMatchingFrame = false
            };

            switch(filter.FilterElementConfiguration)
            {
            case FilterElementConfiguration.RxFIFO0OnMatch:
            {
                var wasStored = StoreInRxFIFO(0, rxBufferElementHeader, rxMessage.Data);
                break;
            }
            case FilterElementConfiguration.RxFIFO1OnMatch:
            {
                var wasStored = StoreInRxFIFO(1, rxBufferElementHeader, rxMessage.Data);
                break;
            }
            case FilterElementConfiguration.RejectIDOnMatch:
            {
                if(filter.SyncMessage)
                {
                    this.Log(LogLevel.Warning, "Reject ID if filter matches is not intended to be used with Sync messages");
                }
                break;
            }
            case FilterElementConfiguration.SetPriorityOnMatch:
            {
                if(filter.SyncMessage)
                {
                    this.Log(LogLevel.Warning, "Setting priority on filter match is not intended to be used with Sync messages");
                    return;
                }

                rv.HighPriorityMessageStatus.MessageStorageIndicator.Value = MessageStorageIndicator.NoFIFOselected;
                rv.HighPriorityMessageStatus.FilterIndex.Value = (ulong)idx;
                rv.HighPriorityMessageStatus.FilterList.Value = filter.IsExtended;

                rv.InterruptRegister.InterruptFlags[(int)Interrupt.HighPriorityMessage].Value = true;
                break;
            }
            case FilterElementConfiguration.SetPriorityAndRxFIFO0OnMatch:
            {
                var wasStored = StoreInRxFIFO(0, rxBufferElementHeader, rxMessage.Data);

                if(wasStored)
                {
                    rv.HighPriorityMessageStatus.BufferIndex.Value = rv.RxFIFO0Status.RxFIFO0PutIndex.Value - 1; // Put Index was already incremented for new message so subtract one
                    rv.HighPriorityMessageStatus.MessageStorageIndicator.Value = MessageStorageIndicator.MessageInFIFO0;
                }
                else
                {
                    rv.HighPriorityMessageStatus.MessageStorageIndicator.Value = MessageStorageIndicator.FIFOMessageLost;
                }
                rv.HighPriorityMessageStatus.FilterIndex.Value = (ulong)idx;
                rv.HighPriorityMessageStatus.FilterList.Value = filter.IsExtended;

                rv.InterruptRegister.InterruptFlags[(int)Interrupt.HighPriorityMessage].Value = true;
                break;
            }
            case FilterElementConfiguration.SetPriorityAndRxFIFO1OnMatch:
            {
                var wasStored = StoreInRxFIFO(1, rxBufferElementHeader, rxMessage.Data);

                if(wasStored)
                {
                    rv.HighPriorityMessageStatus.BufferIndex.Value = rv.RxFIFO1Status.RxFIFO1PutIndex.Value - 1; // Put Index was already incremented for new message so subtract one
                    rv.HighPriorityMessageStatus.MessageStorageIndicator.Value = MessageStorageIndicator.MessageInFIFO1;
                }
                else
                {
                    rv.HighPriorityMessageStatus.MessageStorageIndicator.Value = MessageStorageIndicator.FIFOMessageLost;
                }
                rv.HighPriorityMessageStatus.FilterIndex.Value = (ulong)idx;
                rv.HighPriorityMessageStatus.FilterList.Value = filter.IsExtended;

                rv.InterruptRegister.InterruptFlags[(int)Interrupt.HighPriorityMessage].Value = true;
                break;
            }
            case FilterElementConfiguration.RxBufferOrDebugMessageOnMatch:
            {
                StoreInRxBuffer(rxBufferElementHeader, rxMessage.Data, filter);
                break;
            }
            default:
            {
                this.Log(LogLevel.Warning, "Invalid Filter Element Configuration");
                break;
            }
            }
        }

        private void AcceptNonMatchingFrame(NonMatchingFrameTarget frameTarget, CANMessageFrame rxMessage)
        {
            var rxBufferElementHeader = new RxBufferElementHeader
            {
                Identifier = rxMessage.ExtendedId,
                RemoteTransmissionRequest = rxMessage.RemoteFrame,
                ExtendedIdentifier = rxMessage.ExtendedFormat,
                ErrorStateIndicator = false,
                RxTimestamp = TimestampAt(SentAt(rxMessage)),   // Duet
                DataLengthCode = MapDataBytesCountToDataLengthCode(k: rxMessage.Data.Length),
                BitRateSwitch = rxMessage.BitRateSwitch,
                FDFormat = rxMessage.FDFormat,
                FilterIndex = 0,
                AcceptedNonMatchingFrame = true
            };

            switch(frameTarget)
            {
            case NonMatchingFrameTarget.AcceptInRxFIFO0:
            {
                StoreInRxFIFO(0, rxBufferElementHeader, rxMessage.Data);
                break;
            }
            case NonMatchingFrameTarget.AcceptInRxFIFO1:
            {
                StoreInRxFIFO(1, rxBufferElementHeader, rxMessage.Data);
                break;
            }
            default:
            {
                this.Log(LogLevel.Warning, "Non-matching frame was rejected");
                break;
            }
            }
        }

        private bool StoreInRxFIFO(uint idx, RxBufferElementHeader rxHeader, byte[] rxData)
        {
            if(idx >= NumberOfRxFIFOs)
            {
                this.Log(LogLevel.Warning, "Only FIFO0 or FIFO1 are available");
            }

            var rxFIFO = idx == 0 ? rxFIFO0 : rxFIFO1;

            if(rxFIFO.Size.Value == 0)
            {
                rxFIFO.InterruptMessageLost.Value = true;
                rxFIFO.MessageLost.Value = true;
                return false; // Message is discarded
            }

            var dataFieldInBytes = MapDataFieldSizeToDataBytes(rxFIFO.DataFieldSize.Value);
            var fifoElementSizeInBytes = BufferElementHeaderSizeInBytes + dataFieldInBytes;

            var addr = (rxFIFO.StartAddress.Value << 2) + rxFIFO.RxFIFOPutIndex * (ulong)fifoElementSizeInBytes;
            var rxHeaderBytes = Packet.Encode(rxHeader);

            switch(rxFIFO.OperationMode.Value)
            {
            case FIFOOperationMode.Overwrite:
            {
                if(rxFIFO.Full.Value)
                {
                    rxFIFO.RxFIFOGetIndex += 1;
                }
                break;
            }
            case FIFOOperationMode.Blocking:
            default:
            {
                if(rxFIFO.Full.Value)
                {
                    rxFIFO.InterruptMessageLost.Value = true;
                    rxFIFO.MessageLost.Value = true;
                    return false; // Message is discarded
                }
                break;
            }
            }

            messageRAM.WriteBytes((long)addr, rxHeaderBytes, 0, rxHeaderBytes.Length);
            messageRAM.WriteBytes((long)addr + rxHeaderBytes.Length, rxData, 0, rxData.Length);

            rxFIFO.RxFIFOPutIndex += 1;
            rxFIFO.Full.Value = rxFIFO.RxFIFOPutIndex == rxFIFO.RxFIFOGetIndex;

            var watermarkReached = (rxFIFO.Watermark.Value > 0) && (rxFIFO.RxFIFOFillLevel >= rxFIFO.Watermark.Value);
            if(watermarkReached)
            {
                rxFIFO.InterruptWatermarkReached.Value = true;
            }

            if(rxFIFO.Full.Value)
            {
                rxFIFO.InterruptFull.Value = true;
            }

            rxFIFO.InterruptNewMessage.Value = true;

            return true;
        }

        private void StoreInRxBuffer(RxBufferElementHeader rxHeader, byte[] rxData, MessageIDFilterElement filter)
        {
            var id2 = filter.ID2;

            var rxBufferIdx = BitHelper.GetValue(id2, 0, 6);
            var filterEventPins = BitHelper.GetValue(id2, 6, 3);
            var target = (RxBufferOrDebugDestination)BitHelper.GetValue(id2, 9, 2);

            var startAddress = rxBuffer.StartAddress.Value << 2;
            var dataFieldSize = rxBuffer.DataFieldSize.Value;

            var dataFieldInBytes = MapDataFieldSizeToDataBytes(dataFieldSize);
            var bufferElementSizeInBytes = BufferElementHeaderSizeInBytes + dataFieldInBytes;

            var addr = startAddress + rxBufferIdx * (ulong)bufferElementSizeInBytes;
            var rxHeaderBytes = Packet.Encode(rxHeader);

            messageRAM.WriteBytes((long)addr, rxHeaderBytes, 0, rxHeaderBytes.Length);
            messageRAM.WriteBytes((long)addr + rxHeaderBytes.Length, rxData, 0, rxData.Length);

            switch(target)
            {
            case RxBufferOrDebugDestination.StoreInRxBuffer:
            {
                var newDataFlags = rxBufferIdx < 32 ? rv.NewData1.NewData1Flags : rv.NewData2.NewData2Flags;
                newDataFlags[rxBufferIdx % 32].Value = true;

                rv.InterruptRegister.InterruptFlags[(int)Interrupt.MessageStoredToDedicatedRxBuffer].Value = true;
                break;
            }
            case RxBufferOrDebugDestination.DebugMessageA:
            case RxBufferOrDebugDestination.DebugMessageB:
            case RxBufferOrDebugDestination.DebugMessageC:
            default:
            {
                this.Log(LogLevel.Warning, "DMU add-on is required to activate DMA request output after debug message is stored");
                break;
            }
            }
        }

        private int MapDataFieldSizeToDataBytes(ulong k)
        {
            if(k >= 8)
            {
                this.Log(LogLevel.Warning, "Invalid Data Field Size");
                return 0;
            }

            DataFieldSizeToBytesCountMap.TryGetValue(k, out var fieldSize);
            return fieldSize;
        }

        private byte MapDataBytesCountToDataLengthCode(int k)
        {
            if(k <= 8)
            {
                return (byte)k;
            }

            if(k > 64)
            {
                this.Log(LogLevel.Warning, "Received frame has more than 64 bytes");
                return 0;
            }
            var success = FDBytesCountToDataLengthCodeMap.TryGetValue(k, out var datalengthCode);

            if(!success)
            {
                this.Log(LogLevel.Warning, "Invalid length of received frame");
                return 0;
            }

            return datalengthCode;
        }

        private byte MapDataLengthCodeToDataBytesCount(int k)
        {
            if(k <= 8)
            {
                return (byte)k;
            }

            if(k > 15)
            {
                this.Log(LogLevel.Warning, "Frame specfied an invalid Data Length Code");
                return 0;
            }

            DataLengthCodeToFDBytesCountMap.TryGetValue(k, out var fdBytesCount);
            return fdBytesCount;
        }

        private void UpdateInterrupts()
        {
            var flag0 = false;
            var flag1 = false;

            for(int i = 0; i < rv.InterruptRegister.InterruptFlags.Length; i++)
            {
                if(rv.InterruptEnable.InterruptEnableFlags[i].Value && rv.InterruptRegister.InterruptFlags[i].Value)
                {
                    flag0 |= rv.InterruptLineEnable.EnableInterruptLine0.Value && !rv.InterruptLineSelect.InterruptLineSelectFlags[i].Value;
                    flag1 |= rv.InterruptLineEnable.EnableInterruptLine1.Value && rv.InterruptLineSelect.InterruptLineSelectFlags[i].Value;
                }
            }

            Line0.Set(flag0);
            Line1.Set(flag1);
        }

        // Duet: timestamps

        // When a frame was sent, by the sender's clock, for the receivers to stamp it with
        private static readonly ConditionalWeakTable<CANMessageFrame, StrongBox<double>> sendingTimes = new ConditionalWeakTable<CANMessageFrame, StrongBox<double>>();

        // A frame starts on the bus when it is requested (the bus is assumed idle) and is sent when its
        // last bit is; that is the moment both ends' timestamps describe
        private void RecordSendingTime(CANMessageFrame frame)
        {
            lastTransmissionEnd = Now() + FrameDuration(frame);
            sendingTimes.AddOrUpdate(frame, new StrongBox<double>(lastTransmissionEnd));
        }

        private double SentAt(CANMessageFrame frame)
        {
            return sendingTimes.TryGetValue(frame, out var sent) ? sent.Value : Now();
        }

        // Seconds on the bus: the arbitration and control fields and the end of frame at the nominal
        // bit rate, the data field and CRC at the data bit rate if the frame switches bit rate.
        // Stuff bits are ignored.
        private double FrameDuration(CANMessageFrame frame)
        {
            var nominalBit = (double)(nominalPrescaler.Value + 1) * (nominalTimeSegment1.Value + nominalTimeSegment2.Value + 3) / canClockFrequency;
            var dataBit = frame.BitRateSwitch
                ? (double)(dataPrescaler.Value + 1) * (dataTimeSegment1.Value + dataTimeSegment2.Value + 3) / canClockFrequency
                : nominalBit;
            var headerBits = frame.ExtendedFormat ? 41 : 22;                // SOF, identifier, control bits up to BRS
            var crcBits = frame.FDFormat ? (frame.Data.Length > 16 ? 26 : 22) : 16;   // ESI, DLC... and the CRC
            const int trailerBits = 12;                                      // CRC delimiter, ACK, EOF, intermission
            return (headerBits + trailerBits) * nominalBit + (8 * frame.Data.Length + crcBits) * dataBit;
        }

        // Emulated seconds, as the CPU's timers see them
        private double Now() => machine.ClockSource.CurrentValue.TotalSeconds;

        // The timestamp counter's value at an emulated time, which may be a little in the future
        private ushort TimestampAt(double time)
        {
            switch(timestampSelect?.Value ?? 0)
            {
            case 1:
            {
                var bitClocks = (nominalPrescaler.Value + 1) * (nominalTimeSegment1.Value + nominalTimeSegment2.Value + 3);
                var ticksPerSecond = (double)canClockFrequency / bitClocks / (timestampPrescaler.Value + 1);
                return (ushort)((long)Math.Floor((time - timestampEpoch) * ticksPerSecond) & 0xFFFF);
            }
            case 2:
            {
                if(externalStepClock == null)
                {
                    this.Log(LogLevel.Warning, "External timestamp selected but no externalTimestamp peripheral with a StepClock property was given");
                    return 0;
                }
                var now = Convert.ToInt64(externalStepClock.GetValue(externalTimestamp));
                var ticksAgo = (long)Math.Round((Now() - time) * externalTimestampFrequency);
                return (ushort)((now - ticksAgo) & 0xFFFF);
            }
            default:
                return 0;
            }
        }

        private readonly long canClockFrequency;
        private readonly IPeripheral externalTimestamp;
        private readonly long externalTimestampFrequency;
        private readonly PropertyInfo externalStepClock;
        private IValueRegisterField nominalTimeSegment1;
        private IValueRegisterField nominalTimeSegment2;
        private IValueRegisterField nominalPrescaler;
        private IValueRegisterField timestampSelect;
        private IValueRegisterField timestampPrescaler;
        private IValueRegisterField dataTimeSegment1;
        private IValueRegisterField dataTimeSegment2;
        private IValueRegisterField dataPrescaler;
        private double timestampEpoch;
        private double lastTransmissionEnd;

        private void LoopbackReceive(CANMessageFrame frame)
        {
            machine.HandleTimeDomainEvent(
                OnFrameReceived,
                frame,
                TimeDomainsManager.Instance.GetEffectiveVirtualTimeStamp()
            );
        }
    }
}

namespace Antmicro.Renode.Peripherals.CAN
{
    public partial class DuetMCAN
    {
        public long Size => 0x400;

        public GPIO Line0 { get; private set; }

        public GPIO Line1 { get; private set; }

        public GPIO Calibration { get; private set; }

        public event Action<CANMessageFrame> FrameSent;

        private void BuildStructuredViews()
        {
            rxFIFO0 = new RxFIFOView
            {
                StartAddress = rv.RxFIFO0Configuration.RxFIFO0StartAddress,
                Size = rv.RxFIFO0Configuration.RxFIFO0Size,
                Watermark = rv.RxFIFO0Configuration.RxFIFO0Watermark,
                OperationMode = rv.RxFIFO0Configuration.FIFO0OperationMode,
                GetIndexRaw = rv.RxFIFO0Status.RxFIFO0GetIndex,
                PutIndexRaw = rv.RxFIFO0Status.RxFIFO0PutIndex,
                Full = rv.RxFIFO0Status.RxFIFO0Full,
                MessageLost = rv.RxFIFO0Status.RxFIFO0MessageLost,
                AcknowledgeIndex = rv.RxFIFO0Acknowledge.RxFIFO0AcknowledgeIndex,
                DataFieldSize = rv.RxBufferFIFOElementSizeConfiguration.RxFIFO0DataFieldSize,
                InterruptMessageLost = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO0MessageLost],
                InterruptWatermarkReached = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO0WatermarkReached],
                InterruptFull = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO0Full],
                InterruptNewMessage = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO0NewMessage]
            };

            rxFIFO1 = new RxFIFOView
            {
                StartAddress = rv.RxFIFO1Configuration.RxFIFO1StartAddress,
                Size = rv.RxFIFO1Configuration.RxFIFO1Size,
                Watermark = rv.RxFIFO1Configuration.RxFIFO1Watermark,
                OperationMode = rv.RxFIFO1Configuration.FIFO1OperationMode,
                GetIndexRaw = rv.RxFIFO1Status.RxFIFO1GetIndex,
                PutIndexRaw = rv.RxFIFO1Status.RxFIFO1PutIndex,
                Full = rv.RxFIFO1Status.RxFIFO1Full,
                MessageLost = rv.RxFIFO1Status.RxFIFO1MessageLost,
                AcknowledgeIndex = rv.RxFIFO1Acknowledge.RxFIFO1AcknowledgeIndex,
                DataFieldSize = rv.RxBufferFIFOElementSizeConfiguration.RxFIFO1DataFieldSize,
                InterruptMessageLost = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO1MessageLost],
                InterruptWatermarkReached = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO1WatermarkReached],
                InterruptFull = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO1Full],
                InterruptNewMessage = rv.InterruptRegister.InterruptFlags[(int)Interrupt.RxFIFO1NewMessage]
            };

            filterConfigurationStandard = new FilterConfigurationView
            {
                RejectRemoteFrames = rv.GlobalFilterConfiguration.RejectRemoteFramesStandard,
                AcceptNonMatchingFrames = rv.GlobalFilterConfiguration.AcceptNonMatchingFramesStandard,
                FilterListStartAddress = rv.StandardIDFilterConfiguration.FilterListStandardStartAddress,
                ListSize = rv.StandardIDFilterConfiguration.ListSizeStandard
            };

            filterConfigurationExtended = new FilterConfigurationView
            {
                RejectRemoteFrames = rv.GlobalFilterConfiguration.RejectRemoteFramesExtended,
                AcceptNonMatchingFrames = rv.GlobalFilterConfiguration.AcceptNonMatchingFramesExtended,
                FilterListStartAddress = rv.ExtendedIDFilterConfiguration.FilterListExtendedStartAddress,
                ListSize = rv.ExtendedIDFilterConfiguration.ListSizeExtended
            };

            rxBuffer = new RxBufferView
            {
                StartAddress = rv.RxBufferConfiguration.RxBufferStartAddress,
                DataFieldSize = rv.RxBufferFIFOElementSizeConfiguration.RxBufferDataFieldSize
            };

            txBuffers = new TxBufferView
            {
                StartAddress = rv.TxBufferConfiguration.TxBuffersStartAddress,
                NumberOfDedicatedTxBuffers = rv.TxBufferConfiguration.NumberOfDedicatedTxBuffers,
                TxFIFOQueueSize = rv.TxBufferConfiguration.TxFIFOQueueSize,
                TxFIFOQueueMode = rv.TxBufferConfiguration.TxFIFOQueueMode,
                DataFieldSize = rv.TxBufferElementSizeConfiguration.TxBufferDataFieldSize
            };

            txFIFOQueue = new TxFIFOQueueView
            {
                Offset = rv.TxBufferConfiguration.NumberOfDedicatedTxBuffers,
                Size = rv.TxBufferConfiguration.TxFIFOQueueSize,
                QueueMode = rv.TxBufferConfiguration.TxFIFOQueueMode,
                GetIndexRaw = rv.TxFIFOQueueStatus.TxFIFOGetIndex,
                PutIndexRaw = rv.TxFIFOQueueStatus.TxFIFOQueuePutIndex,
                FullRaw = rv.TxFIFOQueueStatus.TxFIFOQueueFull,
                TransmissionRequestPendingFlags = rv.TxBufferRequestPending.TransmissionRequestPendingFlags
            };

            txEventFIFO = new TxEventFIFOView
            {
                StartAddress = rv.TxEventFIFOConfiguration.EventFIFOStartAddress,
                Size = rv.TxEventFIFOConfiguration.EventFIFOSize,
                Watermark = rv.TxEventFIFOConfiguration.EventFIFOWatermark,
                GetIndexRaw = rv.TxEventFIFOStatus.EventFIFOGetIndex,
                PutIndexRaw = rv.TxEventFIFOStatus.EventFIFOPutIndex,
                Full = rv.TxEventFIFOStatus.EventFIFOFull,
                ElementLost = rv.TxEventFIFOStatus.TxEventFIFOElementLost,
                AcknowledgeIndex = rv.TxEventFIFOAcknowledge.EventFIFOAcknowledgeIndex,
                InterruptNewEntry = rv.InterruptRegister.InterruptFlags[(int)Interrupt.TxEventFIFONewEntry],
                InterruptWatermarkReached = rv.InterruptRegister.InterruptFlags[(int)Interrupt.TxEventFIFOWatermarkReached],
                InterruptFull = rv.InterruptRegister.InterruptFlags[(int)Interrupt.TxEventFIFOFull],
                InterruptElementLost = rv.InterruptRegister.InterruptFlags[(int)Interrupt.TxEventFIFOElementLost],
            };
        }

        private void BuildRegisterMapView()
        {
            rv = new RegisterMapView
            {
                TestRegister = new TestRegister(),
                CCControlRegister = new CCControlRegister(),
                ProtocolStatusRegister = new ProtocolStatusRegister(),
                InterruptRegister = new InterruptRegister(),
                InterruptEnable = new InterruptEnable(),
                InterruptLineSelect = new InterruptLineSelect(),
                InterruptLineEnable = new InterruptLineEnable(),
                GlobalFilterConfiguration = new GlobalFilterConfiguration(),
                StandardIDFilterConfiguration = new StandardIDFilterConfiguration(),
                ExtendedIDFilterConfiguration = new ExtendedIDFilterConfiguration(),
                ExtendedIdANDMask = new ExtendedIdANDMask(),
                HighPriorityMessageStatus = new HighPriorityMessageStatus(),
                NewData1 = new NewData1(),
                NewData2 = new NewData2(),
                RxFIFO0Configuration = new RxFIFO0Configuration(),
                RxFIFO0Status = new RxFIFO0Status(),
                RxFIFO0Acknowledge = new RxFIFO0Acknowledge(),
                RxBufferConfiguration = new RxBufferConfiguration(),
                RxFIFO1Configuration = new RxFIFO1Configuration(),
                RxFIFO1Status = new RxFIFO1Status(),
                RxFIFO1Acknowledge = new RxFIFO1Acknowledge(),
                RxBufferFIFOElementSizeConfiguration = new RxBufferFIFOElementSizeConfiguration(),
                TxBufferConfiguration = new TxBufferConfiguration(),
                TxFIFOQueueStatus = new TxFIFOQueueStatus(),
                TxBufferElementSizeConfiguration = new TxBufferElementSizeConfiguration(),
                TxBufferRequestPending = new TxBufferRequestPending(),
                TxBufferAddRequest = new TxBufferAddRequest(),
                TxBufferCancellationRequest = new TxBufferCancellationRequest(),
                TxBufferTransmissionOccurred = new TxBufferTransmissionOccurred(),
                TxBufferCancellationFinished = new TxBufferCancellationFinished(),
                TxBufferTransmissionInterruptEnable = new TxBufferTransmissionInterruptEnable(),
                TxBufferCancellationFinishedInterruptEnable = new TxBufferCancellationFinishedInterruptEnable(),
                TxEventFIFOConfiguration = new TxEventFIFOConfiguration(),
                TxEventFIFOStatus = new TxEventFIFOStatus(),
                TxEventFIFOAcknowledge = new TxEventFIFOAcknowledge()
            };
        }

        private bool IsProtectedWrite => rv.CCControlRegister.ControlFields[(int)Control.Initialization].Value && rv.CCControlRegister.ControlFields[(int)Control.ConfigurationChangeEnable].Value;

        private RxFIFOView rxFIFO1;
        private TxEventFIFOView txEventFIFO;
        private TxFIFOQueueView txFIFOQueue;
        private RxBufferView rxBuffer;
        private FilterConfigurationView filterConfigurationExtended;
        private FilterConfigurationView filterConfigurationStandard;
        private TxBufferView txBuffers;
        private RxFIFOView rxFIFO0;
        private RegisterMapView rv;
        private readonly DoubleWordRegisterCollection registers;
        private readonly IMultibyteWritePeripheral messageRAM;

        private readonly IMachine machine;

        private const int ExtendedFilterSizeInBytes = 8;
        private const int StandardFilterSizeInBytes = 4;
        private const int BufferElementHeaderSizeInBytes = 8;
        private const int TxEventFIFOElementSizeInBytes = 8;
        private const int NumberOfRxFIFOs = 2;
        private const byte PaddingByte = 0xcc;

        private static readonly IReadOnlyDictionary<ulong, int> DataFieldSizeToBytesCountMap = new Dictionary<ulong, int>
        {
            {0b000, 8},
            {0b001, 12},
            {0b010, 16},
            {0b011, 20},
            {0b100, 24},
            {0b101, 32},
            {0b110, 48},
            {0b111, 64},
        };

        private static readonly IReadOnlyDictionary<int, byte> FDBytesCountToDataLengthCodeMap = new Dictionary<int, byte>
        {
            {12, 9},
            {16, 10},
            {20, 11},
            {24, 12},
            {32, 13},
            {48, 14},
            {64, 15},
        };

        private static readonly IReadOnlyDictionary<int, byte> DataLengthCodeToFDBytesCountMap = new Dictionary<int, byte>
        {
            {9, 12},
            {10, 16},
            {11, 20},
            {12, 24},
            {13, 32},
            {14, 48},
            {15, 64},
        };

        private struct RxFIFO1Acknowledge
        {
            public IValueRegisterField RxFIFO1AcknowledgeIndex;
        }

        private struct HighPriorityMessageStatus
        {
            public IValueRegisterField BufferIndex;
            public IEnumRegisterField<MessageStorageIndicator> MessageStorageIndicator;
            public IValueRegisterField FilterIndex;
            public IFlagRegisterField FilterList;
        }

        private struct NewData1
        {
            public IFlagRegisterField[] NewData1Flags;
        }

        private struct NewData2
        {
            public IFlagRegisterField[] NewData2Flags;
        }

        private struct RxFIFO0Configuration
        {
            public IValueRegisterField RxFIFO0StartAddress;
            public IValueRegisterField RxFIFO0Size;
            public IValueRegisterField RxFIFO0Watermark;
            public IEnumRegisterField<FIFOOperationMode> FIFO0OperationMode;
        }

        private struct RxFIFO0Status
        {
            public IValueRegisterField RxFIFO0GetIndex;
            public IValueRegisterField RxFIFO0PutIndex;
            public IFlagRegisterField RxFIFO0Full;
            public IFlagRegisterField RxFIFO0MessageLost;
        }

        private struct RxFIFO0Acknowledge
        {
            public IValueRegisterField RxFIFO0AcknowledgeIndex;
        }

        private struct RxBufferConfiguration
        {
            public IValueRegisterField RxBufferStartAddress;
        }

        private struct RxFIFO1Configuration
        {
            public IValueRegisterField RxFIFO1StartAddress;
            public IValueRegisterField RxFIFO1Size;
            public IValueRegisterField RxFIFO1Watermark;
            public IEnumRegisterField<FIFOOperationMode> FIFO1OperationMode;
        }

        private struct RxFIFO1Status
        {
            public IValueRegisterField RxFIFO1GetIndex;
            public IValueRegisterField RxFIFO1PutIndex;
            public IFlagRegisterField RxFIFO1Full;
            public IFlagRegisterField RxFIFO1MessageLost;
            public IEnumRegisterField<DebugMessageStatus> DebugMessageStatus;
        }

        private struct RxBufferFIFOElementSizeConfiguration
        {
            public IValueRegisterField RxFIFO0DataFieldSize;
            public IValueRegisterField RxFIFO1DataFieldSize;
            public IValueRegisterField RxBufferDataFieldSize;
        }

        private struct TxBufferCancellationFinished
        {
            public IFlagRegisterField[] CancellationFinishedFlags;
        }

        private struct TxFIFOQueueStatus
        {
            public IValueRegisterField TxFIFOGetIndex;
            public IValueRegisterField TxFIFOQueuePutIndex;
            public IFlagRegisterField TxFIFOQueueFull;
        }

        private struct TxBufferElementSizeConfiguration
        {
            public IValueRegisterField TxBufferDataFieldSize;
        }

        private struct TxBufferRequestPending
        {
            public IFlagRegisterField[] TransmissionRequestPendingFlags;
        }

        private struct TxBufferAddRequest
        {
            public IFlagRegisterField[] AddRequestFlags;
        }

        private struct TxBufferCancellationRequest
        {
            public IFlagRegisterField[] CancellationRequestFlags;
        }

        private struct TxBufferTransmissionOccurred
        {
            public IFlagRegisterField[] TransmissionOccurredFlags;
        }

        private struct ExtendedIdANDMask
        {
            public IValueRegisterField ExtendedIDANDMask;
        }

        private struct TxBufferTransmissionInterruptEnable
        {
            public IFlagRegisterField[] TransmissionInterruptEnableFlags;
        }

        private struct TxBufferCancellationFinishedInterruptEnable
        {
            public IFlagRegisterField[] CancellationFinishedInterruptEnableFlags;
        }

        private struct TxEventFIFOConfiguration
        {
            public IValueRegisterField EventFIFOStartAddress;
            public IValueRegisterField EventFIFOSize;
            public IValueRegisterField EventFIFOWatermark;
        }

        private struct TxBufferConfiguration
        {
            public IValueRegisterField TxBuffersStartAddress;
            public IValueRegisterField NumberOfDedicatedTxBuffers;
            public IValueRegisterField TxFIFOQueueSize;
            public IFlagRegisterField TxFIFOQueueMode;
        }

        private struct ExtendedIDFilterConfiguration
        {
            public IValueRegisterField FilterListExtendedStartAddress;
            public IValueRegisterField ListSizeExtended;
        }

        private struct CCControlRegister
        {
            public IFlagRegisterField[] ControlFields;
        }

        private struct GlobalFilterConfiguration
        {
            public IFlagRegisterField RejectRemoteFramesExtended;
            public IFlagRegisterField RejectRemoteFramesStandard;
            public IEnumRegisterField<NonMatchingFrameTarget> AcceptNonMatchingFramesExtended;
            public IEnumRegisterField<NonMatchingFrameTarget> AcceptNonMatchingFramesStandard;
        }

#pragma warning disable 649, 169
        [LeastSignificantByteFirst]
        private struct RxBufferElementHeader
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 16)]
            public ushort RxTimestamp;                              // RXTS
            [PacketField, Offset(doubleWords:1, bits: 16), Width(bits: 4)]
            public byte DataLengthCode;                             // DLC
            [PacketField, Offset(doubleWords:1, bits: 20), Width(bits: 1)]
            public bool BitRateSwitch;                              // BRS
            [PacketField, Offset(doubleWords:1, bits: 21), Width(bits: 1)]
            public bool FDFormat;                                   // FDF
            [PacketField, Offset(doubleWords:1, bits: 22), Width(bits: 2)]
            private readonly byte Reserved;
            [PacketField, Offset(doubleWords:1, bits: 24), Width(bits: 7)]
            public byte FilterIndex;                                // FIDX
            [PacketField, Offset(doubleWords:1, bits: 31), Width(bits: 1)]
            public bool AcceptedNonMatchingFrame;                   // ANMF
        }

        [LeastSignificantByteFirst]
        private struct RxBufferElementHeaderTSU
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 4)]
            public byte RxTimestampPointer;                         // RXTSP
            [PacketField, Offset(doubleWords:1, bits: 4), Width(bits: 1)]
            public bool TimestampCaptured;                          // TSC
            [PacketField, Offset(doubleWords:1, bits: 5), Width(bits: 11)]
            private readonly ushort Reserved0;
            [PacketField, Offset(doubleWords:1, bits: 16), Width(bits: 4)]
            public byte DataLengthCode;                             // DLC
            [PacketField, Offset(doubleWords:1, bits: 20), Width(bits: 1)]
            public bool BitRateSwitch;                              // BRS
            [PacketField, Offset(doubleWords:1, bits: 21), Width(bits: 1)]
            public bool FDFormat;                                   // FDF
            [PacketField, Offset(doubleWords:1, bits: 22), Width(bits: 2)]
            private readonly byte Reserved1;
            [PacketField, Offset(doubleWords:1, bits: 24), Width(bits: 7)]
            public byte FilterIndex;                                // FIDX
            [PacketField, Offset(doubleWords:1, bits: 31), Width(bits: 1)]
            public bool AcceptedNonMatchingFrame;                   // ANMF
        }

        [LeastSignificantByteFirst]
        private struct TxScanBufferAndEventFIFOCommonHeader
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
        }

        [LeastSignificantByteFirst]
        private struct TxBufferElementHeader
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 8)]
            private readonly byte Reserved;
            [PacketField, Offset(doubleWords:1, bits: 8), Width(bits: 8)]
            public byte MessageMarkerHigh;                          // MM
            [PacketField, Offset(doubleWords:1, bits: 16), Width(bits: 4)]
            public byte DataLengthCode;                             // DLC
            [PacketField, Offset(doubleWords:1, bits: 20), Width(bits: 1)]
            public bool BitRateSwitch;                              // BRS
            [PacketField, Offset(doubleWords:1, bits: 21), Width(bits: 1)]
            public bool FDFormat;                                   // FDF
            [PacketField, Offset(doubleWords:1, bits: 22), Width(bits: 1)]
            public bool TimeStampCaptureEnable;                     // TSCE
            [PacketField, Offset(doubleWords:1, bits: 23), Width(bits: 1)]
            public bool EventFIFOControl;                           // EFC
            [PacketField, Offset(doubleWords:1, bits: 24), Width(bits: 8)]
            public byte MessageMarkerLow;                           // MM
        }

        [LeastSignificantByteFirst]
        private struct TxEventFIFOElement
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 16)]
            public ushort TxTimestamp;                              // TXTS
            [PacketField, Offset(doubleWords:1, bits: 16), Width(bits: 4)]
            public byte DataLengthCode;                             // DLC
            [PacketField, Offset(doubleWords:1, bits: 20), Width(bits: 1)]
            public bool BitRateSwitch;                              // BRS
            [PacketField, Offset(doubleWords:1, bits: 21), Width(bits: 1)]
            public bool FDFormat;                                   // FDF
            [PacketField, Offset(doubleWords:1, bits: 22), Width(bits: 2)]
            public byte EventType;                                  // ET
            [PacketField, Offset(doubleWords:1, bits: 24), Width(bits: 8)]
            public byte MessageMarker;                              // MM
        }

        [LeastSignificantByteFirst]
        private struct TxEventFIFOElementTSU
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint Identifier;                                 // ID
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 1)]
            public bool RemoteTransmissionRequest;                  // RTR
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 1)]
            public bool ExtendedIdentifier;                         // XTD
            [PacketField, Offset(doubleWords:0, bits: 31), Width(bits: 1)]
            public bool ErrorStateIndicator;                        // ESI
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 4)]
            public byte TxTimestampPointer;                          // TXTSP
            [PacketField, Offset(doubleWords:1, bits: 4), Width(bits: 1)]
            public bool TimestampCaptured;                           // TSC
            [PacketField, Offset(doubleWords:1, bits: 5), Width(bits: 3)]
            private readonly bool Reserved;
            [PacketField, Offset(doubleWords:1, bits: 8), Width(bits: 8)]
            public byte MessageMarkerHigh;                           // MM
            [PacketField, Offset(doubleWords:1, bits: 16), Width(bits: 4)]
            public byte DataLengthCode;                              // DLC
            [PacketField, Offset(doubleWords:1, bits: 20), Width(bits: 1)]
            public bool BitRateSwitch;                               // BRS
            [PacketField, Offset(doubleWords:1, bits: 21), Width(bits: 1)]
            public bool FDFormat;                                    // FDF
            [PacketField, Offset(doubleWords:1, bits: 22), Width(bits: 2)]
            public byte EventType;                                   // ET
            [PacketField, Offset(doubleWords:1, bits: 24), Width(bits: 8)]
            public byte MessageMarkerLow;                            // MM
        }

        [LeastSignificantByteFirst]
        private struct StandardMessageIDFilterElement
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 11)]
            public ushort StandardFilterID2;                         // SFID2
            [PacketField, Offset(doubleWords:0, bits: 11), Width(bits: 4)]
            private readonly byte Reserved;
            [PacketField, Offset(doubleWords:0, bits: 15), Width(bits: 1)]
            public bool StandardSyncMessage;                         // SSYNC
            [PacketField, Offset(doubleWords:0, bits: 16), Width(bits: 11)]
            public ushort StandardFilterID1;                         // SFID1
            [PacketField, Offset(doubleWords:0, bits: 27), Width(bits: 3)]
            public FilterElementConfiguration StandardFilterElementConfiguration; // SFEC
            [PacketField, Offset(doubleWords:0, bits: 30), Width(bits: 2)]
            public FilterType StandardFilterType;                    // SFT
        }

        [LeastSignificantByteFirst]
        private struct ExtendedMessageIDFilterElement
        {
            [PacketField, Offset(doubleWords:0, bits: 0), Width(bits: 29)]
            public uint ExtendedFilterID1;                           // EFID1
            [PacketField, Offset(doubleWords:0, bits: 29), Width(bits: 3)]
            public FilterElementConfiguration ExtendedFilterElementConfiguration; // EFEC
            [PacketField, Offset(doubleWords:1, bits: 0), Width(bits: 29)]
            public uint ExtendedFilterID2;                           // EFID2
            [PacketField, Offset(doubleWords:1, bits: 29), Width(bits: 1)]
            public bool ExtendedSyncMessage;                         // ESYNC
            [PacketField, Offset(doubleWords:1, bits: 30), Width(bits: 2)]
            public FilterType ExtendedFilterType;                    // EFT
        }
#pragma warning restore 649, 169

        // StandardMessageIDFilterElement and ExtendedMessageIDFilterElement have different layout in memory,
        // but their fields have the same meaning, so we store them in a common structure for universal access.
        private struct MessageIDFilterElement
        {
            public uint ID1;
            public uint ID2;
            public bool SyncMessage;
            public FilterElementConfiguration FilterElementConfiguration;
            public FilterType FilterType;
            public bool IsExtended;
        }

        // RxFIFOnConfiguration + RxFIFOnStatus + RxFIFOnAcknowledge + RxBufferFIFOElementSizeConfiguration
        // InterruptFlags: RxFIFOnMessageLost + RxFIFOnWatermarkReached + RxFIFOnFull + RxFIFOnNewMessage
        private struct RxFIFOView
        {
            public IValueRegisterField StartAddress;
            public IValueRegisterField Size;
            public IValueRegisterField Watermark;
            public IEnumRegisterField<FIFOOperationMode> OperationMode;

            public IValueRegisterField GetIndexRaw;
            public IValueRegisterField PutIndexRaw;
            public IFlagRegisterField Full;
            public IFlagRegisterField MessageLost;

            public IValueRegisterField AcknowledgeIndex;

            public IValueRegisterField DataFieldSize;

            public IFlagRegisterField InterruptMessageLost;
            public IFlagRegisterField InterruptWatermarkReached;
            public IFlagRegisterField InterruptFull;
            public IFlagRegisterField InterruptNewMessage;

            public ulong RxFIFOFillLevel
            {
                get
                {
                    if(Full.Value)
                    {
                        return Size.Value;
                    }
                    else if(PutIndexRaw.Value >= GetIndexRaw.Value)
                    {
                        return PutIndexRaw.Value - GetIndexRaw.Value;
                    }
                    else
                    {
                        return Size.Value - (GetIndexRaw.Value - PutIndexRaw.Value);
                    }
                }
            }

            public ulong RxFIFOPutIndex
            {
                get
                {
                    return PutIndexRaw.Value;
                }

                set
                {
                    PutIndexRaw.Value = value % Size.Value; // circular queue
                }
            }

            public ulong RxFIFOGetIndex
            {
                get
                {
                    return GetIndexRaw.Value;
                }

                set
                {
                    GetIndexRaw.Value = value % Size.Value; // circular queue
                }
            }
        }

        private struct FilterConfigurationView
        {
            public IFlagRegisterField RejectRemoteFrames;
            public IEnumRegisterField<NonMatchingFrameTarget> AcceptNonMatchingFrames;
            public IValueRegisterField FilterListStartAddress;
            public IValueRegisterField ListSize;
        }

        private struct RxBufferView
        {
            public IValueRegisterField StartAddress;
            public IValueRegisterField DataFieldSize;
        }

        private struct TxFIFOQueueView
        {
            public IValueRegisterField Offset;
            public IValueRegisterField Size;
            public IFlagRegisterField QueueMode;
            public IValueRegisterField GetIndexRaw;
            public IValueRegisterField PutIndexRaw;
            public IFlagRegisterField FullRaw;
            public IFlagRegisterField[] TransmissionRequestPendingFlags;

            public bool Full
            {
                get
                {
                    return FillLevel == Size.Value;
                }
            }

            public ulong FillLevel
            {
                get
                {
                    if(QueueMode.Value)
                    {
                        return Size.Value - FreeLevel;
                    }
                    else if(FullRaw.Value)
                    {
                        return Size.Value;
                    }
                    else if(PutIndexRaw.Value >= GetIndexRaw.Value)
                    {
                        return PutIndexRaw.Value - GetIndexRaw.Value;
                    }
                    else
                    {
                        return Size.Value - (GetIndexRaw.Value - PutIndexRaw.Value);
                    }
                }
            }

            public ulong FreeLevel
            {
                get
                {
                    if(QueueMode.Value)
                    {
                        var offset = (int)Offset.Value;
                        var size = (int)Size.Value;
                        var freeLevel = 0;
                        for(var i = offset; i < offset + size; i++)
                        {
                            if(!TransmissionRequestPendingFlags[i].Value)
                            {
                                freeLevel++;
                            }
                        }
                        return (ulong)freeLevel;
                    }
                    else
                    {
                        return Size.Value - FillLevel;
                    }
                }
            }

            public ulong PutIndex
            {
                get
                {
                    if(QueueMode.Value)
                    {
                        var offset = (int)Offset.Value;
                        var size = (int)Size.Value;
                        var putIndex = 0;
                        for(var i = offset; i < offset + size; i++)
                        {
                            if(!TransmissionRequestPendingFlags[i].Value)
                            {
                                putIndex = i;
                                break;
                            }
                        }
                        return (ulong)putIndex;
                    }
                    else
                    {
                        return PutIndexRaw.Value;
                    }
                }

                set
                {
                    PutIndexRaw.Value = (value - Offset.Value) % Size.Value + Offset.Value;
                }
            }

            public ulong GetIndex
            {
                get
                {
                    return QueueMode.Value ? 0 : GetIndexRaw.Value;
                }

                set
                {
                    GetIndexRaw.Value = (value - Offset.Value) % Size.Value + Offset.Value;
                }
            }
        }

        private struct TxBufferView
        {
            public IValueRegisterField StartAddress;
            public IValueRegisterField NumberOfDedicatedTxBuffers;
            public IValueRegisterField TxFIFOQueueSize;
            public IFlagRegisterField TxFIFOQueueMode;
            public IValueRegisterField DataFieldSize;
        }

        private struct TxEventFIFOView
        {
            public IValueRegisterField StartAddress;
            public IValueRegisterField Size;
            public IValueRegisterField Watermark;
            public IValueRegisterField GetIndexRaw;
            public IValueRegisterField PutIndexRaw;
            public IFlagRegisterField Full;
            public IFlagRegisterField ElementLost;
            public IValueRegisterField AcknowledgeIndex;

            public IFlagRegisterField InterruptNewEntry;
            public IFlagRegisterField InterruptWatermarkReached;
            public IFlagRegisterField InterruptFull;
            public IFlagRegisterField InterruptElementLost;

            public ulong FillLevel
            {
                get
                {
                    if(Full.Value)
                    {
                        return Size.Value;
                    }
                    else if(PutIndexRaw.Value >= GetIndexRaw.Value)
                    {
                        return PutIndexRaw.Value - GetIndexRaw.Value;
                    }
                    else
                    {
                        return Size.Value - (GetIndexRaw.Value - PutIndexRaw.Value);
                    }
                }
            }

            public ulong PutIndex
            {
                get
                {
                    return PutIndexRaw.Value;
                }

                set
                {
                    PutIndexRaw.Value = value % Size.Value; // circular queue
                }
            }

            public ulong GetIndex
            {
                get
                {
                    return GetIndexRaw.Value;
                }

                set
                {
                    GetIndexRaw.Value = value % Size.Value; // circular queue
                }
            }
        }

        private struct RegisterMapView
        {
            public TestRegister TestRegister;
            public CCControlRegister CCControlRegister;
            public ProtocolStatusRegister ProtocolStatusRegister;
            public InterruptRegister InterruptRegister;
            public InterruptEnable InterruptEnable;
            public InterruptLineSelect InterruptLineSelect;
            public InterruptLineEnable InterruptLineEnable;
            public GlobalFilterConfiguration GlobalFilterConfiguration;
            public StandardIDFilterConfiguration StandardIDFilterConfiguration;
            public ExtendedIDFilterConfiguration ExtendedIDFilterConfiguration;
            public ExtendedIdANDMask ExtendedIdANDMask;
            public HighPriorityMessageStatus HighPriorityMessageStatus;
            public NewData1 NewData1;
            public NewData2 NewData2;
            public RxFIFO0Configuration RxFIFO0Configuration;
            public RxFIFO0Status RxFIFO0Status;
            public RxFIFO0Acknowledge RxFIFO0Acknowledge;
            public RxBufferConfiguration RxBufferConfiguration;
            public RxFIFO1Configuration RxFIFO1Configuration;
            public RxFIFO1Status RxFIFO1Status;
            public RxFIFO1Acknowledge RxFIFO1Acknowledge;
            public RxBufferFIFOElementSizeConfiguration RxBufferFIFOElementSizeConfiguration;
            public TxBufferConfiguration TxBufferConfiguration;
            public TxFIFOQueueStatus TxFIFOQueueStatus;
            public TxBufferElementSizeConfiguration TxBufferElementSizeConfiguration;
            public TxBufferRequestPending TxBufferRequestPending;
            public TxBufferAddRequest TxBufferAddRequest;
            public TxBufferCancellationRequest TxBufferCancellationRequest;
            public TxBufferTransmissionOccurred TxBufferTransmissionOccurred;
            public TxBufferCancellationFinished TxBufferCancellationFinished;
            public TxBufferTransmissionInterruptEnable TxBufferTransmissionInterruptEnable;
            public TxBufferCancellationFinishedInterruptEnable TxBufferCancellationFinishedInterruptEnable;
            public TxEventFIFOConfiguration TxEventFIFOConfiguration;
            public TxEventFIFOStatus TxEventFIFOStatus;
            public TxEventFIFOAcknowledge TxEventFIFOAcknowledge;
        }

        private struct TestRegister
        {
            public IFlagRegisterField LoopBackMode;
            public IValueRegisterField TxBufferNumberPrepared;
            public IFlagRegisterField PreparedValid;
            public IValueRegisterField TxBufferNumberStarted;
            public IFlagRegisterField StartedValid;
        }

        private struct TxEventFIFOStatus
        {
            public IValueRegisterField EventFIFOGetIndex;
            public IValueRegisterField EventFIFOPutIndex;
            public IFlagRegisterField EventFIFOFull;
            public IFlagRegisterField TxEventFIFOElementLost;
        }

        private struct ProtocolStatusRegister
        {
            public IEnumRegisterField<LastErrorCode> LastErrorCode;
            public IEnumRegisterField<Activity> Activity;
            public IFlagRegisterField ReceivedCANFDMessage;
        }

        private struct InterruptRegister
        {
            public IFlagRegisterField[] InterruptFlags;
        }

        private struct InterruptEnable
        {
            public IFlagRegisterField[] InterruptEnableFlags;
        }

        private struct InterruptLineSelect
        {
            public IFlagRegisterField[] InterruptLineSelectFlags;
        }

        private struct InterruptLineEnable
        {
            public IFlagRegisterField EnableInterruptLine0;
            public IFlagRegisterField EnableInterruptLine1;
        }

        private struct StandardIDFilterConfiguration
        {
            public IValueRegisterField FilterListStandardStartAddress;
            public IValueRegisterField ListSizeStandard;
        }

        private struct TxEventFIFOAcknowledge
        {
            public IValueRegisterField EventFIFOAcknowledgeIndex;
        }

        private enum TxScanModeInternal
        {
            Dedicated,
            FIFO,
            Queue,
            MixedDedicatedFIFO,
            MixedDedicatedQueue,
        }

        private enum RxBufferOrDebugDestination
        {
            StoreInRxBuffer = 0b00,
            DebugMessageA = 0b01,
            DebugMessageB = 0b10,
            DebugMessageC = 0b11
        }

        private enum LastErrorCode
        {
            NoError = 0,
            StuffError = 1,
            FormError = 2,
            AckError = 3,
            Bit1Error = 4,
            Bit0Error = 5,
            CRCError = 6,
            NoChange = 7
        }

        private enum Activity
        {
            Synchronizing = 0b00,
            Idle = 0b01,
            Receiver = 0b10,
            Transmitter = 0b11
        }

        private enum FIFOOperationMode
        {
            Blocking = 0,
            Overwrite = 1
        }

        private enum DebugMessageStatus
        {
            IdleState = 0b00,
            DebugMessageA = 0b01,
            DebugMessageAB = 0b10,
            DebugMessageABC = 0b11
        }

        private enum NonMatchingFrameTarget
        {
            AcceptInRxFIFO0 = 0b00,
            AcceptInRxFIFO1 = 0b01,
            Reject_ = 0b10,
            Reject__ = 0b11
        }

        private enum MessageStorageIndicator
        {
            NoFIFOselected = 0b00,
            FIFOMessageLost = 0b01,
            MessageInFIFO0 = 0b10,
            MessageInFIFO1 = 0b11,
        }

        private enum Interrupt
        {
            RxFIFO0NewMessage = 0,                                  // RF0N
            RxFIFO0WatermarkReached = 1,                            // RF0W
            RxFIFO0Full = 2,                                        // RF0F
            RxFIFO0MessageLost = 3,                                 // RF0L
            RxFIFO1NewMessage = 4,                                  // RF1N
            RxFIFO1WatermarkReached = 5,                            // RF1W
            RxFIFO1Full = 6,                                        // RF1F
            RxFIFO1MessageLost = 7,                                 // RF1L
            HighPriorityMessage = 8,                                // HPM
            TransmissionCompleted = 9,                              // TC
            TransmissionCancellationFinished = 10,                  // TCF
            TxFIFOEmpty = 11,                                       // TFE
            TxEventFIFONewEntry = 12,                               // TEFN
            TxEventFIFOWatermarkReached = 13,                       // TEFW
            TxEventFIFOFull = 14,                                   // TEFF
            TxEventFIFOElementLost = 15,                            // TEFL
            TimestampWraparound = 16,                               // TSW
            MessageRAMAccessFailure = 17,                           // MRAF
            TimeoutOccurred = 18,                                   // TOO
            MessageStoredToDedicatedRxBuffer = 19,                  // DRX
            BitErrorCorrected = 20,                                 // BEC
            BitErrorUncorrected = 21,                               // BEU
            ErrorLoggingOverflow = 22,                              // ELO
            ErrorPassive = 23,                                      // EP
            WarningStatus = 24,                                     // EW
            BusOffStatus = 25,                                      // BO
            WatchdogInterrupt = 26,                                 // WDI
            ProtocolErrorInArbitrationPhase = 27,                   // PEA
            ProtocolErrorInDataPhase = 28,                          // PED
            AccessToReservedAddress = 29                            // ARA
        }

        private enum Control
        {
            Initialization = 0,                                     // INIT
            ConfigurationChangeEnable = 1,                          // CCE
            RestrictedOperationMode = 2,                            // ASM
            ClockStopAcknowledge = 3,                               // CSA
            ClockStopRequest = 4,                                   // CSR
            BusMonitoringMode = 5,                                  // MON
            DisableAutomaticRetransmission = 6,                     // DAR
            TestModeEnable = 7,                                     // TEST
            FDOperationEnable = 8,                                  // FDOE
            BitRateSwitchEnable = 9,                                // BRSE
            UseTimestampingUnit = 10,                               // UTSU
            WideMessageMarker = 11,                                 // WMM
            ProtocolExceptionHandlingDisable = 12,                  // PXHD
            EdgeFilteringDuringBusIntegration = 13,                 // EFBI
            TransmitPause = 14,                                     // TXP
            NonISOOperation = 15                                    // NISO
        }

        private enum Register
        {
            CoreReleaseRegister = 0x000,                            // CREL
            EndianRegister = 0x004,                                 // ENDN
            CustomerRegister = 0x008,                               // CUST
            DataBitTimingAndPrescalerRegister = 0x00c,              // DBTP
            TestRegister = 0x010,                                   // TEST
            RAMWatchdog = 0x014,                                    // RWD
            CCControlRegister = 0x018,                              // CCCR
            NominalBitTimingAndPrescalerRegister = 0x01C,           // NBTP
            TimestampCounterConfiguration = 0x020,                  // TSCC
            TimestampCounterValue = 0x024,                          // TSCV
            TimeoutCounterConfiguration = 0x028,                    // TOCC
            TimeoutCounterValue = 0x02C,                            // TOCV
            ErrorCounterRegister = 0x040,                           // ECR
            ProtocolStatusRegister = 0x044,                         // PSR
            TransmitterDelayCompensationRegister = 0x048,           // TDCR
            InterruptRegister = 0x050,                              // IR
            InterruptEnable = 0x054,                                // IE
            InterruptLineSelect = 0x058,                            // ILS
            InterruptLineEnable = 0x05C,                            // ILE
            GlobalFilterConfiguration = 0x080,                      // GFC
            StandardIDFilterConfiguration = 0x084,                  // SIDFC
            ExtendedIDFilterConfiguration = 0x088,                  // XIDFC
            ExtendedIdANDMask = 0x090,                              // XIDAM
            HighPriorityMessageStatus = 0x094,                      // HPMS
            NewData1 = 0x098,                                       // NDAT1
            NewData2 = 0x09C,                                       // NDAT2
            RxFIFO0Configuration = 0x0A0,                           // RXF0C
            RxFIFO0Status = 0x0A4,                                  // RXF0S
            RxFIFO0Acknowledge = 0x0A8,                             // RXF0A
            RxBufferConfiguration = 0x0AC,                          // RXBC
            RxFIFO1Configuration = 0x0B0,                           // RXF1C
            RxFIFO1Status = 0x0B4,                                  // RXF1S
            RxFIFO1Acknowledge = 0x0B8,                             // RXF1A
            RxBufferFIFOElementSizeConfiguration = 0x0BC,           // RXESC
            TxBufferConfiguration = 0x0C0,                          // TXBC
            TxFIFOQueueStatus = 0x0C4,                              // TXFQS
            TxBufferElementSizeConfiguration = 0x0C8,               // TXESC
            TxBufferRequestPending = 0x0CC,                         // TXBRP
            TxBufferAddRequest = 0x0D0,                             // TXBAR
            TxBufferCancellationRequest = 0x0D4,                    // TXBCR
            TxBufferTransmissionOccurred = 0x0D8,                   // TXBTO
            TxBufferCancellationFinished = 0x0DC,                   // TXBCF
            TxBufferTransmissionInterruptEnable = 0x0E0,            // TXBTIE
            TxBufferCancellationFinishedInterruptEnable = 0x0E4,    // TXBCIE
            TxEventFIFOConfiguration = 0x0F0,                       // TXEFC
            TxEventFIFOStatus = 0x0F4,                              // TXEFS
            TxEventFIFOAcknowledge = 0x0F8                          // TXEFA
        }

        private enum FilterType
        {
            Range = 0b00,
            DualID = 0b01,
            Classic = 0b10,
            RangeWithoutMask = 0b11 // valid only for Extended Filter
        }

        private enum FilterElementConfiguration
        {
            DisableFilter = 0b000,
            RxFIFO0OnMatch = 0b001,
            RxFIFO1OnMatch = 0b010,
            RejectIDOnMatch = 0b011,
            SetPriorityOnMatch = 0b100,
            SetPriorityAndRxFIFO0OnMatch = 0b101,
            SetPriorityAndRxFIFO1OnMatch = 0b110,
            RxBufferOrDebugMessageOnMatch = 0b111
        }
    }
}
