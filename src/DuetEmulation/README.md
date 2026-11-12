# Emulation bench

Runs the firmware images this repository builds, unmodified, in the [Renode](https://renode.io) emulator: a main board and any number of expansion boards, each its own emulated MCU, on one emulated CAN bus. The main board runs either RepRapFirmware standalone or DuetCANMaster driven by DuetControlServer, which runs in-process. Tests send codes and observe what the hardware would show: the replies, the object model, every CAN frame, and the boards' pins.

| Board | Firmware preset | MCU | Notes |
| --- | --- | --- | --- |
| MB6HC, standalone | `Duet3Firmware_MB6HC` | SAME70Q20B | Console on Aux2 (USART2), SD card |
| MB6HC, with DSF | `DuetCANMaster_MB6HC` | SAME70Q20B | SBC SPI port (SPI1) bridged to DuetControlServer |
| EXP3HC | `Duet3Firmware_EXP3HC` | SAME51N19A | Version 1.03; boots through `Duet3Bootloader-SAME5x`; any address its switches can set |
| EXP1HCL | `Duet3Firmware_EXP1HCL` | SAME51G19A | Version 2.0; boots through the bootloader; at its default address, 123; open loop only |

## Running

Renode, its SVD files and the SD card tools are installed in the devcontainer (`.devcontainer/install-renode.sh`).

```bash
scripts/test.py emulation --build            # build the images, then run every emulation test
scripts/test.py emulation --stack dsf        # the scenarios on DuetControlServer only
scripts/test.py emulation --filter 'FullyQualifiedName~Heater'
```

Each fixture keeps its files in `build/emulation/runs/<fixture>`: Renode's own log (`renode.log`), the emulated machines' log (`emulation.log`), the CAN and I/O traces (`can.jsonl`, `io.jsonl`), the console (`main-console.txt`, standalone) and DuetControlServer's log (`dcs.log`, DSF).

| Variable | |
| --- | --- |
| `DUET_EMULATION_DEBUG=1` | Run the `-debug` builds of the firmware |
| `DUET_EMULATION_DEBUG_LOG=main/spi1,exp1/can1` | Peripherals whose debug log goes to `emulation.log` |
| `RENODE` | The Renode executable, if not `/opt/renode/renode` |

## Writing tests

A test starts a `Bench` with the boards and the `config.g` it needs, and the stack to run them on:

```csharp
[TestFixture(Stack.Rrf, Category = nameof(Stack.Rrf))]
[TestFixture(Stack.Dsf, Category = nameof(Stack.Dsf))]
[Category("Emulation")]
public class MyTests(Stack stack)
{
    private Bench _bench = null!;

    [OneTimeSetUp]
    public async Task StartAsync() =>
        _bench = await Bench.StartAsync(stack, new Dictionary<int, BoardType> { [1] = BoardTypes.Exp3hc },
                                        "M564 H0 S0\nM584 X1.0\nM92 X80\n", Runs.Directory(this));

    [OneTimeTearDown]
    public async Task StopAsync() => await _bench.DisposeAsync();

    [Test]
    public async Task AMoveSteps()
    {
        _bench.Can.SetMark();
        await _bench.SendCodeAsync("G1 X10 F600");
        await _bench.SendCodeAsync("M400");
        Assert.That(await _bench.Io.StepCountAsync(1, 0), Is.EqualTo(800));
        Assert.That(_bench.Can.Find<CanMessageMovementLinearShaped>(destination: 1), Is.Not.Empty);
    }
}
```

Booting takes tens of seconds of host time, so a fixture boots once and its tests share it. Each stack adds its own first line of `config.g` (`M575` to open the standalone console, `M953` to enable CAN under DSF). Under DSF every driver is on an expansion board, so a configuration that is to run on both stacks puts its axes there.

| `Bench` | |
| --- | --- |
| `SendCodeAsync(code)` | Send a code and return its reply and severity once it has finished |
| `StartCodeAsync(code)` | Send a code without waiting, for a test that acts while it runs; `WaitAsync()` on the result waits for it |
| `GetObjectModelAsync(key)` | The object model at a key, as M409 takes them (`boards[1].drivers[0]`) |
| `WaitAsync(time)`, `WaitUntilAsync(condition, timeout)` | Let emulated time pass |
| `Can` | Every CAN frame, decoded with DSF's generated message types; `SetMark` makes queries consider only later frames |
| `WaitForFrameAsync`, `WaitForReplyAsync(request)` | Wait for a frame, or for the whole standard reply to a request |
| `System.Io` | Every change of the boards' outputs in emulated time (STEP pins are counted, not traced) |
| `Io.OutputAsync(address, port)` | What an output drives now: low, high, PWM with its duty and frequency, or nothing |
| `Io.DriveAsync(address, port, level)`, `ReleaseAsync` | Drive an input, as a switch or sensor would |
| `Io.SetAnalogAsync`, `SetThermistorAsync` | What an analogue input reads |
| `Io.StepCountAsync(address, driver)` | Step pulses a driver has had |
| `Io.AttachThermalModelAsync(address, heater, thermistor, model)` | Model a heater heating a mass its thermistor reads; `ThermalModel.M307` gives the firmware the matching parameters |

Pins are named by the port names their board's firmware gives them (`io1.in`, `out0`, `temp0`). The bench reads them from the firmware's pin table, so it cannot drift from the firmware it runs.

A scenario that uses something a stack does not have yet calls `KnownGap.On(stack, Stack.Dsf, "...")`, which skips it on that stack, naming the gap.

## How it works

`Emulator/RenodeInstance` runs a headless Renode and drives it through its robot server, the XML-RPC interface Renode's own test runner uses, which reports each monitor command's success or failure. Each board is a Renode machine made from its platform description (`Renode/Platforms`) and the peripheral models it needs (`Renode/Peripherals`), which Renode compiles when the bench starts; the image is loaded where the real board has it, after its bootloader if it has one. The machines' CAN controllers are connected to one CAN hub, whose traffic `CanBusLogger` writes to `can.jsonl`.

The machines take turns, a millisecond of emulated time each. Expansion boards synchronise their step clocks to the main board's with CAN timestamps, so the CAN model (`DuetMCAN`) stamps each frame with the time it finished on the bus by the sender's clock: the boards' clock sync has no jitter from the emulator, and a frame's latency of up to a turn does not disturb it.

**Standalone.** Emulated time passes only while a call waits for something, so a test is deterministic: it does the same thing at the same emulated times on every run (`DeterminismTests` checks this). Codes go in on the console UART, each followed by an `echo` of a marker that shows where its reply ends; the object model comes back through `M409`.

**DSF.** DuetControlServer runs in-process with the real `libduet_realtime_core.so`, its SPI transport's bus and lines carried over a Unix socket (DuetRealtimeCore's `Hardware/Emulator`) to `SAME70_SbcSpi`, the model of the SBC's SPI port, so DuetCANMaster's own SPI code runs. DuetControlServer runs in real time and the emulator several times slower, so the bench keeps the emulation running, and every frame from the emulator carries the emulated time, which `EmulatedTimeBase` makes the SBC's clock for StepTimer. Moves then land as they do on hardware, but a run is not repeatable to the microsecond, and DuetControlServer's link timeouts are scaled up. Making DuetControlServer take its time from a `TimeProvider` would let both be measured in emulated time and make this bench deterministic too.

**Pins.** The SAME5x PORT model works out what each pin drives and records each change in `io.jsonl` through `DuetIoTrace`. The TC and TCC models report their outputs' duty and frequency, clocked by their GCLK channel at the frequency the GCLK model works out from what the firmware set up. Driven inputs go to the EIC model, which interrupts as the firmware configured it. The bench tells the PORT model which timer output and EXTINT line each pin has, from the firmware's pin table (`Boards/PinWiring`). `DuetThermalModel` is the heater model M307 describes, run in the machine's emulated time.

Anything a firmware accesses that has no model falls back to the SVD's reset values and is logged as a warning in `emulation.log`, which is the list of what to model next.

## Adding a board

1. Write `Renode/Platforms/<board>.repl`. A SAME5x board includes `SAME51.repl` with `using` and says what is connected: pin straps, what its analogue inputs read, its part's DID.
2. Add a `BoardType` to `Boards/BoardTypes.cs`: its preset and bootloader, CAN controller, address switches or default address, and the header with its pin table, and its preset to `PRESETS` in `scripts/common/emulation_tests.py`.
3. Boot it and read `emulation.log`: a CPU that is stuck is usually spinning on a status bit, and the warnings say which peripheral it is reading. Model it, and repeat.
4. Add scenarios.

## Not modelled yet

- **Stepper driver communication.** TMC drivers do not answer, so driver status and stall detection mean nothing. Step pulses are counted on the STEP pins.
- **Closed loop.** The EXP1HCL's encoders (AS5047, quadrature through PDEC) are not modelled.
- **The main board's pins.** The SAME70 PIO has no interrupts and its PWM and TC outputs are not traced, so standalone tests drive and observe pins on expansion boards.
- **Tool boards.** The SAMC21 and RP2040 boards need models of their own.
- **An EXP1HCL at another address.** Storing an address in a switchless board's user area, as M954 does, is not done yet.
- **USB and the network.**

## Credits

The SAME5x models, the CAN logger and the platforms started from the DuetFirmware `chore/emulation` proof of concept. The SAME70 peripheral models and the MB6HC platform started from Sébastien Metrot's [duet3-emulation](https://github.com/meeloo/duet3-emulation) (MIT, see `Renode/Peripherals/SAME70/LICENSE`). `DuetMCAN.cs` is Antmicro's MCAN model from Renode 1.17.0 (MIT) with timestamps added; its header lists the changes.
