# Emulation bench

Runs the firmware images this repository builds, unmodified, in the [Renode](https://renode.io) emulator: a main board and any number of expansion boards, each its own emulated MCU, on one emulated CAN bus. The main board runs either RepRapFirmware standalone or DuetCANMaster driven by DuetControlServer, which runs in-process. Both stacks are deterministic: the same script does the same things at the same emulated times on every run.

The bench is for seeing exactly what a stack does for a scenario, and for comparing RepRapFirmware with DSF: a run records every code and reply, every CAN frame (decoded), every change of the object model and of the boards' pins, and every input it drove, and two runs can be compared step by step. Inputs can be scripted to change by themselves, so endstops, probes and fan tachos behave as on a machine.

| Board | Firmware preset | MCU | Notes |
| --- | --- | --- | --- |
| MB6HC, standalone | `Duet3Firmware_MB6HC` | SAME70Q20B | Console on Aux2 (USART2), SD card |
| MB6HC, with DSF | `DuetCANMaster_MB6HC` | SAME70Q20B | SBC SPI port (SPI1) bridged to DuetControlServer |
| EXP3HC | `Duet3Firmware_EXP3HC` | SAME51N19A | Version 1.03; boots through `Duet3Bootloader-SAME5x`; any address its switches can set |
| EXP1HCL | `Duet3Firmware_EXP1HCL` | SAME51G19A | Version 2.0; boots through the bootloader; at its default address, 123; open loop only |
| TOOL1RR | `Duet3Firmware_TOOL1RR` | SAME51G19A | Boots through the bootloader; at its default address, 121 |

Renode, its SVD files and the SD card tools are installed in the devcontainer (`.devcontainer/install-renode.sh`). The firmware images must have been built: `scripts/test.py emulation --build` builds them.

## Scenarios and captures

A scenario is a YAML file: the boards, `config.g`, and the steps to run.

```yaml
name: an endstop stops a homing move
stacks: [rrf, dsf]            # the default
boards:
  1: EXP3HC                   # board type by CAN address
config: |                     # config.g, after the stack's own first line
  M584 X1.0
  M92 X80
  M574 X1 S1 P"1.io0.in"
files:                        # further files for the SD card
  sys/homex.g: |
    G91
    G1 H1 X-100 F3000
steps:
  - code: G91                 # send a code and wait for it to finish
  - drive: { board: 1, port: io0.in, level: high, afterSteps: { driver: 0, steps: 2000 } }
  - code: G1 H1 X-100 F3000
    timeout: 20s              # 10s by default
  - code: M114
```

| Step | |
| --- | --- |
| `code: G28` | Send a code and wait for it to finish (`timeout:` to allow longer than 10s) |
| `start: G1 X100` | Send a code without waiting, to act while it runs |
| `await: G1 X100` | Wait for a code sent with `start` |
| `wait: 500ms` | Let emulated time pass (`us`, `ms`, `s`, `min`; seconds without a unit) |
| `note: probing` | A remark in the capture, which also starts a step when captures are compared |
| `drive: { board, port, level }` | Drive a digital input `high` or `low`; with `afterSteps: { driver, steps }`, only once that driver on the same board has made so many more steps |
| `release: { board, port }` | Stop driving an input, leaving it to its pull resistor |
| `analog: { board, port, value }` | Make an analogue input read a fraction of its reference |
| `thermistor: { board, port, celsius }` | Make a thermistor input read a temperature |
| `pulses: { board, port, frequency }` | Drive an input as a square wave; 0 stops it |
| `tacho: { board, port, fan, maxRpm, pulsesPerRev, inverted }` | Pulse a tacho input as the fan on output `fan` turns: its duty times `maxRpm` (6000), `pulsesPerRev` (2) pulses a revolution |

Ports are named as the board's firmware names them (`io0.in`, `out3`, `out3.tach`, `temp0`); the bench reads them from the firmware's pin table. `Scenarios/Examples` has scenarios to start from (a move, endstop homing, a fan's tacho), which `ExampleScenarioTests` runs on both stacks.

```bash
scripts/emulate.py run homing.yaml               # run on each stack, save the captures and compare them
scripts/emulate.py run homing.yaml --stack rrf   # on one stack
scripts/emulate.py show build/emulation/scenarios/<name>/rrf
scripts/emulate.py diff build/emulation/scenarios/<name>/rrf build/emulation/scenarios/<name>/dsf
```

A run's files go in `build/emulation/scenarios/<name>/<stack>`: the capture (`capture.jsonl`) and its report (`capture.txt`) beside the run's logs and traces, and `differences.txt` above them when both stacks ran.

**The capture** is JSON Lines: a header, then one event per line in emulated time order, each with its `kind` and its time `t` in seconds since the boards were created. Events at the same time are in the order they happened.

| `kind` | |
| --- | --- |
| `om-snapshot` | The whole object model when the capture started |
| `code`, `reply` | A code sent, and its reply and severity once it finished |
| `await`, `wait` | The script waiting for a code it started, or letting emulated time pass |
| `can` | A CAN frame: source, destination, type, response, raw `data`, and the `message` decoded with DSF's generated CAN types (a generic message by parameter letter), less any fields and array entries past the end of the frame |
| `om` | An object model value that changed (`path` as an M409 key, `old`, `new`), seen when the code that changed it finished, after each wait and before each input |
| `pin` | An output pin with a port name changed what it drives: Low, High, Pwm (with duty and frequency), Peripheral or Input |
| `input` | An input the script drove |
| `input-change` | An input that changed by itself, as a script set it to (an endstop triggering after its steps, a tacho starting to pulse) |
| `note` | A remark |

The object model leaves out what changes with time rather than with the machine (`state.time`, `state.upTime`, `state.msUpTime`), what describes the computer rather than the machine (`sbc`, `network`, `volumes`), and `inputs[].lineNumber`, which RepRapFirmware advances with the bench's own M409 queries. Standalone, the bench reads the object model the way Duet Web Control does: each key in full when its sequence number changes, and the live values every time. DSF's codes are sent on Aux2, the channel the standalone console is, so the two stacks' `inputs[]` describe the same channel.

**Comparing** pairs the two captures' steps in order (a step starts at each code sent or awaited, wait, input or note, so each step of a scenario is one step of its capture; what comes before the first is `start`):

- replies must match, step by step;
- the object model is compared by state: at the end of each step, every path either stack changed in it must have the same value in both, numbers within 0.1%;
- CAN frames and pin changes are compared as one sequence each over the whole capture, aligned so that one missing frame is one difference, and a frame one stack sends a moment later than the other, in the next step, is still the same frame;
- timing is ignored unless `--time-tolerance` is given.

By default the comparison leaves out the periodic CAN messages (TimeSync, the status and fan reports), the fields each stack fills in its own way (`RequestId`, `WhenToExecute`, `When`) and the main board's description (`boards[0]`); `--ignore-path`, `--ignore-can-type` and `--no-pins` change that.

## Sessions

A session boots the boards once and runs steps one at a time, on both stacks side by side, answering each with what it did on each stack and how they differ. It is the way to explore: a boot takes a minute, a step a few seconds.

```bash
scripts/emulate.py serve &                       # the session server, on localhost:8770
scripts/emulate.py start --board 1=EXP3HC --board 121=TOOL1RR --config 'M584 X1.0'
                                                 # boot these boards with these lines of config.g
scripts/emulate.py start session.yaml            # or a scenario's boards and config.g; its steps run at once
scripts/emulate.py send 'M584 X1.0' 'G1 X10'     # codes, each waited for
scripts/emulate.py drive 1 io0.in high           # also release, analog, thermistor
scripts/emulate.py wait 500ms
scripts/emulate.py step '{drive: {board: 1, port: io0.in, level: high, afterSteps: {driver: 0, steps: 800}}}'
scripts/emulate.py om move.axes                  # the object model at a key, on each stack
scripts/emulate.py capture --text                # everything recorded so far
scripts/emulate.py differences                   # how the stacks differ so far
scripts/emulate.py save                          # save the captures and their differences
scripts/emulate.py stop
```

A session needs no scenario file: `start` on its own boots both stacks with no expansion boards and an empty `config.g`, and `--board ADDRESS=TYPE` and `--config LINE` (each repeatable), `--stack` and `--name` describe the rest. A scenario file is for what a session needs files or setup steps for. `--json` prints the server's answers as they are. The server's HTTP API takes JSON or YAML and answers JSON:

| Request | |
| --- | --- |
| `GET /` | The session: its stacks, boards, emulated times and event counts |
| `POST /session` | Start a session from a scenario, which may be empty; its steps run at once |
| `DELETE /session` | End it, saving the captures |
| `POST /steps` | Run a step, or a list of steps, on every stack |
| `GET /om?key=...` | The object model at a key on every stack |
| `GET /capture?since=N&format=text` | The events recorded, from the Nth on |
| `GET /differences` | How the stacks' captures differ so far |
| `POST /save` | Save the captures and their differences, answering where |

Sessions' files go in `build/emulation/sessions/<name>`.

## Tests

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
| `System.Io` | Every change of the boards' outputs in emulated time (STEP pins are counted, not traced), and every scripted change of an input |
| `Io.OutputAsync(address, port)` | What an output drives now: low, high, PWM with its duty and frequency, or nothing |
| `Io.DriveAsync(address, port, level)`, `ReleaseAsync` | Drive an input, as a switch or sensor would |
| `Io.DriveAfterStepsAsync(address, driver, steps, port, level)` | Drive an input once a driver on the same board has made so many more steps |
| `Io.PulsesAsync(address, port, frequency)`, `TachoFollowsAsync` | Pulse an input, at a fixed rate or as a fan output's tacho |
| `Io.SetAnalogAsync`, `SetThermistorAsync` | What an analogue input reads |
| `Io.StepCountAsync(address, driver)` | Step pulses a driver has had |
| `Io.AttachThermalModelAsync(address, heater, thermistor, model)` | Model a heater heating a mass its thermistor reads; `ThermalModel.M307` gives the firmware the matching parameters |

`Captures.Recorder` runs codes and inputs on a bench and records them in a capture; `Scenarios.ScenarioRunner` runs a scenario on a stack. A scenario that uses something a stack does not have yet calls `KnownGap.On(stack, Stack.Dsf, "...")`, which skips it on that stack, naming the gap.

## How it works

`Emulator/RenodeInstance` runs a headless Renode and drives it through its robot server, the XML-RPC interface Renode's own test runner uses, which reports each monitor command's success or failure. Each board is a Renode machine made from its platform description (`Renode/Platforms`) and the peripheral models it needs (`Renode/Peripherals`), which Renode compiles when the bench starts; the image is loaded where the real board has it, after its bootloader if it has one. The machines' CAN controllers are connected to one CAN hub, whose traffic `CanBusLogger` writes to `can.jsonl`.

The machines take turns, a millisecond of emulated time each. Expansion boards synchronise their step clocks to the main board's with CAN timestamps, so the CAN model (`DuetMCAN`) stamps each frame with the time it finished on the bus by the sender's clock: the boards' clock sync has no jitter from the emulator, and a frame's latency of up to a turn does not disturb it.

**Standalone.** Emulated time passes only while a call waits for something. Codes go in on the console UART, each followed by an `echo` of a marker that shows where its reply ends; the object model comes back through `M409`.

**DSF.** DuetControlServer runs in-process with the real `libduet_realtime_core.so`, its SPI transport's bus and lines carried over a Unix socket (DuetRealtimeCore's `Hardware/Emulator`) to `SAME70_SbcSpi`, the model of the SBC's SPI port, so DuetCANMaster's own SPI code runs. The SBC side runs in lockstep with the emulation (`Dsf/SbcLockstep`):

- Emulated time is the only clock either side reads: DuetControlServer takes every delay, timeout and clock reading from its `TimeProvider`, which here is `Dsf/EmulatedTimeProvider`, and DuetRealtimeCore takes its local time base (`Platform/LocalClock.h`), which the step-clock model, the transfer timeouts and the keep-alive all read, from a pinned clock the bench moves.
- The SBC side takes no emulated time. Before each run of the emulation the bench waits until it has settled: every native thread and DuetControlServer's dispatcher waiting with no wake-up outstanding (DuetRealtimeCore's `Platform/Activity.h`), and nothing queued on or running in the thread pool.
- Between runs the emulator applies the exchanges the SBC has sent (`SAME70_SbcSpi`'s `Sync`), and the frames it sent back are released to the SBC one at a time, each once the SBC's clocks have been moved to the time it was sent at, as are DuetControlServer's timers that fall due, in time order, the SBC settling after each.

The emulation runs a quantum at a time while the SBC link is busy, and in longer slices, up to 8ms and never past DuetControlServer's next timer, while it is idle, because each run costs far more real time than a quantum of emulation. What the SBC does in reply to a frame reaches the emulator at the end of the slice that delivered it, so it sees the controller up to a millisecond late while the link is busy, and up to a slice late when a frame breaks a quiet spell. The bench reads DuetControlServer's object model under its lock, and runs the emulation while it waits for the lock, because a code may hold it while it waits for a board.

**Pins.** The SAME5x PORT model works out what each pin drives and records each change in `io.jsonl` through `DuetIoTrace`. The TC and TCC models report their outputs' duty and frequency, clocked by their GCLK channel at the frequency the GCLK model works out from what the firmware set up. Driven inputs go to the EIC model, which interrupts as the firmware configured it. The bench tells the PORT model which timer output and EXTINT line each pin has, from the firmware's pin table (`Boards/PinWiring`). Scripted inputs live in the PORT model too, so they change at exact emulated times: an edge trigger counts a STEP pin's rising edges, a pulse train is a timer on the machine's clock, and a tacho re-times its pulse train whenever its fan output changes. `DuetThermalModel` is the heater model M307 describes, run in the machine's emulated time.

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
- **SAMC21 boards.** The TOOL1LC and EXP1XD need SAMC21 models of their own.
- **I2C devices.** The TOOL1RR's accelerometer, LDC1612 and filament monitor do not answer.
- **A switchless board at another address.** Storing an address in an EXP1HCL's or TOOL1RR's user area, as M954 does, is not done yet.
- **A step trigger on another board.** An input can be triggered by the steps of a driver on its own board only.
- **USB and the network.**

## Credits

The SAME5x models, the CAN logger and the platforms started from the DuetFirmware `chore/emulation` proof of concept. The SAME70 peripheral models and the MB6HC platform started from Sébastien Metrot's [duet3-emulation](https://github.com/meeloo/duet3-emulation) (MIT, see `Renode/Peripherals/SAME70/LICENSE`). `DuetMCAN.cs` is Antmicro's MCAN model from Renode 1.17.0 (MIT) with timestamps added; its header lists the changes.
