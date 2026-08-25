# System emulation: a staged virtual test bench

The whole stack, DuetControlServer and libduet_sbc on the host with DuetCANMaster and Duet3Expansion
as firmware, running and testable with no hardware on the bench. The work is staged so that each
stage is a usable test rig on its own: stage 1 replaces the controller with a scriptable fake and
already covers the job lifecycle end to end; stage 2 puts the real DuetCANMaster firmware into the
loop under Renode; stage 3 does the same for the expansion boards and completes the chain down to
observable step pulses.

This bench complements the unit tests rather than replacing them. Unit tests stay the tool for fast,
deterministic coverage of one seam; the bench exists for the behaviour that only appears when the
real components talk to each other: link recovery, CAN bridging, clock authority, and the job
lifecycle driven through the real motion engine.

Out of scope at every stage: electromechanical fidelity. TMC driver internals, closed-loop control
and analog physics are only as real as the peripheral models, so tests assert firmware logic, never
physics. Host-side real-time behaviour (transfer jitter, step timing observed from the host) is also
out of scope; the harness in
[src/DuetSbcInterface/harness](../../src/DuetSbcInterface/harness/main.cpp) and the Pi workflow
remain the tools for that.

---

## 1. What exists today

**The transport seam carries two transports.**
[Transport.h](../../src/DuetSbcInterface/src/Interface/Transport.h) is the contract `LinkService`
drives; it never names SPI, and
[TransportFactory.cpp](../../src/DuetSbcInterface/src/Interface/TransportFactory.cpp) is the one
place a concrete transport is chosen. `TransportKind` in
[Configuration.h](../../src/DuetSbcInterface/src/Config/Configuration.h) selects `Spi` or `Socket`.
The contract's header names the three things a transport must answer: the framing is a fixed-size
full-duplex lockstep exchange, flow control is out of band (a pin on SPI), and firmware update
bypasses the protocol once IAP runs. What is common to the lockstep transports - the packet
buffers, CRC bookkeeping and the retry/recovery skeleton - lives in
[FullDuplexExchangeTransport](../../src/DuetSbcInterface/src/Interface/FullDuplexExchangeTransport.h), which
`SpiTransfer` and `SocketTransport` both derive from. The only SPI leak past the seam is the
`dynamic_cast<const SpiTransfer*>` pin diagnostics in
[CApi.cpp](../../src/DuetSbcInterface/src/CApi.cpp), which report zero for any other transport.

**The device side has a second-transport precedent.** `SbcTransportType { spi, Usb }` in
[SbcMessageFormats.h](../../src/DuetCANMaster/src/SBC/SbcMessageFormats.h) and
`DataTransfer::DoTransferUsb` show what a non-SPI framing looks like from DuetCANMaster's side.

**The peer owns time.** `SpiTransferHeader` carries `masterClock` and `hiccupTime` in every
transfer ([MessageFormats.h](../../lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h));
the SBC has no step clock of its own, it fits one to those samples and schedules every move by
absolute start time in the result. Move retirement, and with it deferred-code wake-up and feedhold
outcomes, follows from the clock the controller reports. A fake controller that advances its
reported clock only when a test tells it to therefore makes the motion timeline scriptable. This is
the central design lever of stage 1. One nuance the SBC side adds: its model extrapolates between
samples at the nominal rate and is clamped never to run backwards
([StepTimer.cpp](../../src/DuetSbcInterface/src/Motion/StepTimer.cpp)), so freezing the master clock
alone does not freeze the modelled one. Full determinism pairs the stepped clock with the pinned
local time base (`DuetSbc_PinLocalClock` in [CApi.h](../../src/DuetSbcInterface/src/CApi.h)),
advancing both together.

**DuetCANMaster is a board that has already been emulated.** The
[duet3-emulation](https://github.com/meeloo/duet3-emulation) project runs RepRapFirmware images on
an emulated Duet 3 MB6HC (ATSAME70Q20B) under Renode, with peripheral models for the step-clock
timer, PIO (step/direction observation), analog front end, HSMCI SD card and reset controller.
DuetCANMaster builds `Duet3Firmware_MB6HC` / `MB6XD` for the same part
([CMakeLists.txt](../../src/DuetCANMaster/CMakeLists.txt)), so those models largely carry over. The
project's stated gaps matter here: CAN expansion is unsupported and XDMAC interrupts were
deliberately omitted.

**Renode has the bus plumbing.** One emulation hosts many machines in a shared, deterministic
virtual time; CAN controllers connect to a common
[`CANHub`](https://renode.readthedocs.io/en/latest/networking/machine-to-machine-connections.html),
FD frames included, and a
[SocketCAN bridge](https://renode.readthedocs.io/en/latest/host-integration/can.html) can expose the
emulated bus as a host `vcan` interface. Both DuetCANMaster (SAME70 MCAN) and Duet3Expansion
(SAME5x/SAMC21 CAN) drive the same Bosch M_CAN IP through CoreN2G's `CanDevice`, so one peripheral
model serves every machine on the bus.

**Stage 1 is the bench that exists.** The socket transport, the `ScriptedCanMaster` endpoint and
the `SystemTests` host below are implemented; the framing they speak is defined in
[SocketLinkFormats.h](../../lib/DuetSpiInterface/include/DuetSpiProtocol/SocketLinkFormats.h) and
its executable specification is the loopback peer in
[SocketTransportTests.cpp](../../src/DuetSbcInterface/tests/SocketTransportTests.cpp). No Renode
emulation infrastructure exists yet; stages 2 and 3 are unstarted.

---

## 2. The link into the virtual controller

One decision shapes all three stages: the SBC reaches the virtual controller through a **socket
transport speaking the existing transfer framing**, not through a model of the SPI slave hardware.

The alternative, modeling SPI1 slave mode plus the XDMAC paths that
[DataTransfer.cpp](../../src/DuetCANMaster/src/SBC/DataTransfer.cpp) programs directly, plus a
host-to-Renode SPI bridge that Renode does not provide off the shelf, buys timing fidelity the
protocol does not need: the link is handshake-gated, not timing-critical. The socket transport
instead maps the contract's three named problems directly:

- **Lockstep exchange** becomes one length-delimited frame per direction per transfer, each carrying
  the `SpiTransferHeader` and data block verbatim. Keeping the header intact, sequence numbers,
  CRCs, `dataLength`, `masterClock`, `hiccupTime` and all, means the real protocol state machine
  runs on both sides, captures decode with the existing C# mirrors under
  [Link/Protocol](../../src/DuetControlServer/Link/Protocol/), and the same framing serves the
  Renode peripheral in stage 2 unchanged. The CRCs are computed and checked exactly as on SPI, so
  the validation, resync and resend paths run for real, and a test can corrupt one deliberately.
- **Out-of-band flow control** becomes an explicit readiness message from the peer in place of the
  transfer-ready pin edge; `WaitForTransferReason` blocks on the socket instead of a GPIO fd.
- **IAP** is answered per stage: the stage 1 fake accepts and discards segments (§3), stage 2 maps
  the bare gated transfers onto the same framed exchange so the real flashing path runs against
  emulated flash (§4).

On the firmware side the same framing lands as a third `SbcTransportType` alongside the USB
precedent, fed by a small custom Renode peripheral rather than by SPI1. Unlike the USB framing,
which drops the format code, protocol version and CRCs, this one keeps the header: stage 2's point
is to exercise the real protocol logic, version checks and resync included.

---

## 3. Stage 1: fake DuetCANMaster endpoint

**Goal:** DuetControlServer and libduet_sbc run unmodified on the host, connected to a scriptable
fake controller. Every transfer in both directions is captured for assertions; every response the
protocol expects has a default the fake gives unprompted, and tests override or inject at will.

The real components in the loop are everything above the link: the whole of DCS, and the whole of
libduet_sbc including the motion engine, so `DDARing`, the feedhold, and `ScheduleMove` packet
generation are genuine. What the fake replaces is only what real hardware does with those packets.

### The socket transport

- New `TransportKind::Socket` and a `SocketTransport` implementing
  [Transport.h](../../src/DuetSbcInterface/src/Interface/Transport.h), reusing `TransferTimeout` /
  `TransferError` so the loop's recovery paths run unchanged.
- Transport selection and endpoint address added to `Config`, to `NativeConfig` in
  [NativeMethods.cs](../../src/DuetControlServer/Link/Native/NativeMethods.cs), and to the DCS
  `Settings`, alongside the existing SPI device settings rather than replacing them.
- The `dynamic_cast<const SpiTransfer*>` diagnostics in
  [CApi.cpp](../../src/DuetSbcInterface/src/CApi.cpp) guarded by transport kind; pin diagnostics
  report zero on a transport with no pins, as `MaxPinWaitDurationMs` already documents.

### The fake endpoint

A C# library in the test tree implementing the controller side of the framed exchange. Its default
responses, indexed by [`SbcRequest`](../../lib/DuetSpiInterface/include/DuetSpiProtocol/MessageFormats.h):

| Request | Default behaviour |
|---|---|
| `EmergencyStop`, `Reset` | Acknowledge; drop connection state so the resync path runs |
| `ConfigCAN`, `EnableCAN` | Acknowledge success |
| `ScheduleMove` | Accept and record; the move exists thereafter only in the clock policy below |
| `SendCANMessage` | `CanMessageSent(Ok)`; a scripted `CANResponse` when a test registered one for the message type |
| `WriteIap`, `StartIap` | Accept and discard; flashing is stage 2's to test |
| `Message` | Record |

The fake owns `masterClock`. Two policies, per test: **stepped**, where the clock advances only when
the test says so and a test retires moves up to a chosen point by advancing past their scheduled
start times, giving fully deterministic motion timelines; and **free-running**, where the clock
tracks host time for soak-style runs.

Injection covers the firmware-to-SBC direction: `MotionStopped` with chosen drivers and move id (an
endstop or stall stop, from the link's point of view), `CANResponse` frames (a driver error, heater
fault or filament event as an expansion board would report it, feeding
[EventProcessor](../../src/DuetControlServer/Events/EventProcessor.cs) the real way),
`CodeBufferUpdate`, and `ResendPacket` to exercise the retransmission path. Failure modes are
scripted the same way: a non-`Ok` `CanStatus` on a message the SBC sent, a corrupted CRC, or
readiness withheld until the transfer times out. This artificial error injection is the fake's
permanent role: emulated firmware cannot be made to produce these on demand, so the fake endpoint
remains part of the bench through stages 2 and 3 rather than being superseded by them.

Capture is total: every transfer's header and every packet, both directions, in order, exposed to
NUnit assertions and dumpable to a log that decodes with the existing protocol mirrors. The Saleae
decoder at [tools/saleae-spi-hla](../../tools/saleae-spi-hla/HighLevelAnalyzer.py) is the reference
for what a readable rendering of that capture looks like.

### The test host

The `src/SystemTests` NUnit project (separate from `src/UnitTests`, which stays fast and
link-free) builds the DCS generic host in-process with the real `NativeLink` and real
`libduet_sbc.so`, pointed at the fake endpoint, with a per-test virtual SD tree
(`Host/DcsTestHost.cs`). The enabling seams: `InternalsVisibleTo` for `JobProcessor` and friends,
the configurable SD root (`Settings.BaseDirectory`), transfer timeouts taken from `Settings` so a
debugger-paused test does not trip the reconnect path, and the pinned local clock described in §1.

Running the bench is one command:

```sh
cd src/SystemTests && dotnet test
```

Building the test project also builds the host `libduet_sbc.so` (a CMake configure on first use,
then an incremental build that is a no-op when the native sources are unchanged), so the library
can never be stale relative to the C++ it was built from. `Host/NativeLibraryLocator.cs` resolves
the freshest host build from the CMake tree at run time - no copy step is involved. Opt out of the
native build with `-p:BuildNativeLink=false`, or pin a specific library by setting
`DUET_SBC_LIBRARY`, which skips the build too so nothing rebuilds underneath the pin. Test configs enable the CAN
bus first: a config code that sends CAN traffic before `M953` is answered with `BusError`, exactly
as DuetCANMaster answers a send with no CAN device.

### What stage 1 unlocks

The job lifecycle end to end against the real motion engine: select a file, print, `M25` with a real
feedhold whose purge outcome the clock policy makes deterministic, the pause macros, the restore
point, `M24` with `MoveFractionToSkip` replay, `M226`/`M600`, deferred codes waking on retirement,
event pauses from injected CAN traffic, and cancel/abort. Everything
[JOB_LIFECYCLE.md](JOB_LIFECYCLE.md) marks 🔧 for hardware verification gets a first automated home
here, with hardware retaining only what involves real motion.

- [x] `SocketTransport` and `TransportKind::Socket`, with the loop's recovery semantics preserved
      (validated by the loopback suite in `SocketTransportTests.cpp`)
- [x] Config, C ABI and `Settings` plumbing for transport selection (`SbcTransport`,
      `SbcSocketPath`)
- [x] SPI-only diagnostics in `CApi` guarded by transport kind (the `dynamic_cast` reports zero)
- [x] Fake endpoint: default response table, stepped and free-running clock policies, and the
      `CanEnabled` gate answering sends on a disabled bus with `BusError` as DuetCANMaster does
- [x] Injection: `MotionStopped`, `CANResponse`, `CodeBufferUpdate`, `ResendPacket`, plus
      `InjectStandardReply`/`AckCanRequestsWithStandardReplies` built on the DCS CAN mirrors
- [x] Scripted failure modes: non-`Ok` `CanStatus`, corrupted CRCs, withheld readiness
      (`PauseArming`/`ResumeArming`), `SimulateReboot`
- [x] Total capture with typed decoding for assertions and a dumpable exchange log
- [x] `SystemTests` project hosting DCS in-process against the fake
- [x] The enabling seams: `InternalsVisibleTo`, the configurable SD root (`BaseDirectory`),
      timeouts from `Settings`, and the pinned local clock (`DuetSbc_PinLocalClock`)
- [x] First scenarios: boot and keep-alive, CRC corruption retried without a resync, reconnect and
      reconfigure after a controller reboot, withheld readiness recovering, injected traffic
      reaching the dispatcher, a commanded move decoded as its `ScheduleMove`,
      pause/resume/restore/cancel through the whole job lifecycle, and a pause interrupting a
      blocking `M116` with the fake playing the heater's board (its status reports are the only
      source of heater state and temperature, so the bench owns the whole heating loop)
- [ ] Scenarios still to write: deferred codes waking on retirement, event pause from injected CAN
      traffic, `MotionStopped` closing a homing move, resend-request replay, non-`Ok` send status
      surfacing, and the stepped clock paired with the pinned local time base for a fully
      deterministic timeline
- [ ] CI wiring for `SystemTests` (the native library builds automatically as part of the test
      project, so a CI job needs only cmake, a host toolchain and `dotnet test`)

The first scenario runs surfaced real DuetControlServer defects the unit tests could not see, all
in the pause path: the file task read `PauseState` back at `NotPaused` as "the print finished", a
read-ahead code cancelled by the pause was turned into a job abort, the deferred-pause check ran on
the token the pause had just cancelled and took the file task down with it, and the pause's
cancellation of the read-ahead reached nothing at all because `Code.ExecuteAsync` overwrote the
token the job loop had assigned - which both let purged moves keep executing while "pausing" and
hung a pause behind a blocking `M116`. All are fixed in `JobProcessor` and the heating scenario
also exposed the heater-mode mapping defect fixed in `ExpansionBoardManager` (a board's
`HeaterMode` was cast straight to a `HeaterState`).

---

## 4. Stage 2: DuetCANMaster emulated, expansion faked on the bus

**Goal:** the real `Duet3Firmware_MB6HC` image boots under Renode and carries the link; the SBC side
is unchanged from stage 1 apart from the endpoint it connects to. Below the CANMaster sits a fake
expansion board speaking just enough CAN to keep it satisfied.

The emulation assets, platform descriptions, peripheral models, user-row images, Renode scripts and
Robot scenarios, live in a fork of duet3-emulation added as a git submodule at `tools/emulation`,
the same arrangement as [src/Duet3Expansion](../../src/Duet3Expansion/): versioned alongside the
firmware they test, developed on their own. duet3-emulation is MIT licensed, so carrying its MB6HC
platform work into the fork is unencumbered; the fork repository is provided when stage 2 work
starts.

### DuetCANMaster under Renode

- Rework the duet3-emulation MB6HC platform for this firmware: the SAME70 core, timers, PIO, HSMCI
  and reset models carry over; the WiFi-shared SPI1 and the RRF-specific expectations do not.
- A custom link peripheral presenting the stage 1 socket framing to the host on one side and an
  interrupt-driven mailbox to the firmware on the other, plus the matching `SbcTransportType` branch
  in [DataTransfer.cpp](../../src/DuetCANMaster/src/SBC/DataTransfer.cpp). This keeps the omitted
  XDMAC modeling unnecessary.
- A Bosch M_CAN peripheral model wired as MCAN1, message RAM included, since `CanDevice` allocates
  Tx buffers, FIFOs and filters in RAM the controller reads directly. This model is shared with
  stage 3 and can be built in parallel with everything else.
- IAP over the framed link: the gated bare transfers mapped onto the same framing so `M997` flashes
  emulated flash for real.

### The fake expansion board

A stub machine inside the emulation, joined to the same `CANHub` as the CANMaster, so that every
frame it sends and receives stays in virtual time. It speaks the minimum of
[CAN_PROTOCOL](../../src/Duet3Expansion/docs/devel/CAN_PROTOCOL.md): announce itself for
enumeration, consume time sync, acknowledge movement and heater messages, answer status requests
with static values. It doubles as the bus capture and injection point for tests at this stage.

Renode is itself a .NET application and custom peripherals are C# classes, so the stub is a Renode
plugin that references the same typed CAN structs the unit tests already verify
([CanMessageLayout](../../src/UnitTests/Link/CanMessageLayout.g.cs)); the fake's protocol logic
exists once, shared between the test assemblies and the emulation. NUnit reaches it through a small
control channel (a TCP socket the plugin listens on) to configure responses, inject frames and pull
the capture. That channel is shared stage 2/3 infrastructure, not stage 2 overhead: stage 3's tests
need the same path to script pins, pause machines and read the sniffer.

Keeping the fake inside the emulation is what makes the interesting stage 2 assertions
reproducible: timeout, ordering and resend tests are claims about latency, and only virtual time
makes latency identical on every run. It also keeps CI free of the `vcan` kernel module and the
privileges SocketCAN needs. The SocketCAN bridge is not the tests' substrate at any stage; it is
the human-facing tap for candump and Wireshark while debugging (one bridge per bus segment; more
create frame loops).

### What stage 2 adds

The real protocol semantics between SBC and controller: sequence numbers, resync, resend, version
checks, reset detection; real clock authority, with `masterClock` coming from the emulated step
timer rather than a test script; the real CAN bridging code in
[CanInterface.cpp](../../src/DuetCANMaster/src/CAN/CanInterface.cpp) and its four tasks; board
enumeration surfacing in the DCS object model; and the firmware-update path. The trade against
stage 1 is determinism at the host boundary: DCS runs in wall-clock time against a peer in virtual
time, so transfer timeouts loosen and assertions stay functional rather than timing-exact.

- [ ] MB6HC platform description and peripheral set reworked for DuetCANMaster
- [ ] Link peripheral speaking the stage 1 framing; `SbcTransportType` branch in `DataTransfer`
- [ ] M_CAN model with message RAM, wired as MCAN1
- [ ] IAP mapped onto the framed link; `M997` against emulated flash
- [ ] Stub expansion machine: enumeration, time sync, acks, static status; capture and injection
- [ ] The NUnit control channel into the emulation, built to carry stage 3's scripting too
- [ ] Stage 1 scenario suite re-run against the emulated controller

---

## 5. Stage 3: expansion boards emulated

**Goal:** real `Duet3Firmware_EXP3HC` images on the bus, one Renode machine per board, completing
the chain from G-code in DCS to step pulses observable on an emulated pin.

- A SAME51N19A platform for the EXP3HC: Cortex-M4F core (standard Renode fare), SERCOM, TC/TCC step
  generation, DMAC, ADC, the shared M_CAN model, and NVM including the user row, which is where the
  firmware reads its CAN address and timing, so each board's identity is just a distinct user-row
  image. A SAMC21 tool-board platform (TOOL1LC) follows the same pattern later.
- All machines join one `CANHub` with the CANMaster; the stage 2 stub machine remains on the bus as
  the injection tap, for the same reason stage 1's fake endpoint survives: malformed or unexpected
  traffic has to come from somewhere scriptable.
- Test taps: an in-emulation hook or sniffer peripheral on the hub, seeing every frame with a
  virtual timestamp, deterministic and fit for ordering assertions ("the movement-stop reached both
  boards before any further movement message"), read from NUnit over the stage 2 control channel;
  Renode's GPIO/pin observation for step pulses and for scripting endstops, probes and filament
  sensors, which duet3-emulation left static; and the SocketCAN bridge for candump and Wireshark
  while debugging, never as the tests' substrate.
- Fault scenarios multiple machines make possible, each a Monitor command: pause one board mid-print
  (board hang), disconnect it from the hub (wiring failure), hold it in reset through a reboot (late
  enumeration), mixed board types.
- [Renode's Robot Framework integration](https://renode.readthedocs.io/en/latest/introduction/testing.html)
  drives scripted scenarios headlessly in CI; the NUnit `SystemTests` suite keeps owning the
  assertions that live on the DCS side.

- [ ] SAME51N19A platform: core, SERCOM, TC/TCC, DMAC, ADC, NVM user row, shared M_CAN
- [ ] Per-board user-row images; multi-machine `CANHub` script
- [ ] In-emulation CAN sniffer with virtual timestamps, log format decodable by the test tree
- [ ] Endstop/probe/filament-sensor pin scripting; step pulse observation
- [ ] Fault scenario suite: hang, disconnect, reset-hold, mixed boards
- [ ] Robot Framework scenarios and CI wiring
- [ ] SAMC21 (TOOL1LC) platform, once the EXP3HC platform has proven the approach

---

## 6. Sequencing

Stage 1 blocks stage 2's link work (the framing must exist before a Renode peripheral can speak it)
but not the M_CAN model or the SAME51 platform, which are independent and can start any time.
Stage 2 blocks stage 3 only through the CANMaster machine existing; the stub expansion machine
and the M_CAN model are shared between them. Every stage leaves the previous stage's rig intact:
stage 1's fake endpoint is a permanent part of the bench, because it is the only configuration
where the motion timeline is fully scripted and where error responses, non-`Ok` statuses, corrupted
CRCs and withheld readiness, can be injected on demand; emulated firmware cannot be made to
misbehave to order.
